﻿#region copyright

// -----------------------------------------------------------------------
// <copyright file="ConsulDiscoveryService.cs" company="Akka.NET Project">
//    Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//    Copyright (C) 2013-2017 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
// -----------------------------------------------------------------------

#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Consul;

namespace Akka.Cluster.Discovery.Consul
{
    public class ConsulDiscoveryService : LockingDiscoveryService
    {
        #region internal classes

        /// <summary>
        /// Message scheduled by <see cref="ConsulDiscoveryService"/> for itself. 
        /// Used to trigger periodic restart of consul client.
        /// </summary>
        public sealed class RestartClient
        {
            public static RestartClient Instance { get; } = new RestartClient();

            private RestartClient()
            {
            }
        }

        #endregion

        private readonly ConsulSettings settings;
        private readonly ICancelable restartTask;
        private IConsulClient consul;
        private IDistributedLock distributedLock;

        private readonly string protocol;

        public ConsulDiscoveryService(Config config) : this(new ConsulSettings(config))
        {
            protocol = ((ExtendedActorSystem) Context.System).Provider.DefaultAddress.Protocol;
        }

        public ConsulDiscoveryService(ConsulSettings settings)
            : this(CreateConsulClient(settings), settings)
        {
        }

        public ConsulDiscoveryService(IConsulClient consulClient, ConsulSettings settings) : base(settings)
        {
            consul = consulClient;
            this.settings = settings;

            var restartInterval = settings.RestartInterval;
            if (restartInterval.HasValue && restartInterval.Value != TimeSpan.Zero)
            {
                var scheduler = Context.System.Scheduler;
                restartTask = scheduler.ScheduleTellRepeatedlyCancelable(restartInterval.Value, restartInterval.Value,
                    Self, RestartClient.Instance, Self);
            }
        }

        protected override void Ready()
        {
            base.Ready();
            Receive<RestartClient>(_ =>
            {
                Log.Debug("Restarting consul client...");

                consul.Dispose();
                consul = CreateConsulClient(settings);
            });
        }

        protected override async Task<bool> LockAsync(string key)
        {
            distributedLock = await consul.AcquireLock(key);
            return distributedLock.IsHeld;
        }

        protected override async Task UnlockAsync(string key)
        {
            await distributedLock.Release();
        }

        protected override async Task<IEnumerable<Address>> GetNodesAsync(bool onlyAlive)
        {
            var services = await consul.Health.Service(Context.System.Name);

            var result =
                from x in services.Response
                where !onlyAlive || Equals(x.Checks[1].Status, HealthStatus.Passing)
                select Address.Parse(protocol + "://" + x.Service.ID);

            return result;
        }

        protected override async Task RegisterNodeAsync(MemberEntry node)
        {
            if (!node.Address.Port.HasValue)
                throw new ArgumentException($"Cluster address {node.Address} doesn't have a port specified");

            var address = node.Address;
            var id = ServiceId(address);
            var registration = new AgentServiceRegistration
            {
                ID = id,
                Name = node.ClusterName,
                Tags = node.Roles.ToArray(),
                Address = node.Address.Host,
                Port = node.Address.Port.Value,
                Check = new AgentServiceCheck
                {
                    TTL = settings.ServiceCheckTtl,
                    DeregisterCriticalServiceAfter = settings.AliveTimeout,
                }
            };

            // first, try to deregister service, if it has been registered previously
            await consul.Agent.ServiceRegister(registration);

            Log.Info("Registered node [{0}] as consul service [{1}] (TTL: {2})", node, id, settings.AliveInterval);
        }

        protected override async Task DeregisterNodeAsync(MemberEntry node)
        {
            var id = ServiceId(node.Address);
            await consul.Agent.ServiceDeregister(id);

            Log.Info("Deregistered node [{0}] from consul.", node);
        }

        protected override async Task MarkAsAliveAsync(MemberEntry node)
        {
            var addr = node.Address;
            await consul.Agent.PassTTL("service:" + ServiceId(addr), string.Empty);
        }

        private static string ServiceId(Address addr)
        {
            return $"{addr.System}@{addr.Host}:{addr.Port}/";
        }

        protected override void PostStop()
        {
            base.PostStop();
            restartTask?.Cancel();
            consul.Dispose();
        }

        private static ConsulClient CreateConsulClient(ConsulSettings settings) => new ConsulClient(config =>
        {
            config.Address = settings.ListenerUrl;
            config.Datacenter = settings.Datacenter;
            config.Token = settings.Token;
            config.WaitTime = settings.WaitTime;
        });
    }
}