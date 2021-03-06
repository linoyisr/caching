﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using PubComp.Caching.Core;
using PubComp.Caching.Core.Notifications;
using PubComp.Caching.RedisCaching.Converters;
using StackExchange.Redis;

namespace PubComp.Caching.RedisCaching
{
    public class RedisCacheNotifier : ICacheNotifier, IDisposable
    {
        private readonly string name;
        private readonly RedisCacheNotifierPolicy policy;
        private readonly string connectionString;
        private readonly IRedisConverter convert;
        private readonly string sender;
        private readonly NLog.ILogger log;
        
        private ConcurrentDictionary<string, RedisClient> cacheSubClients;
        private ConcurrentDictionary<string, Func<CacheItemNotification, bool>> cacheCallbacks;

        public RedisCacheNotifier(string name, RedisCacheNotifierPolicy policy)
        {
            this.name = name;
            this.policy = policy;

            this.log = NLog.LogManager.GetLogger(typeof(RedisCacheNotifier).FullName);

            this.cacheSubClients = new ConcurrentDictionary<string, RedisClient>();
            this.cacheCallbacks = new ConcurrentDictionary<string, Func<CacheItemNotification, bool>>();

            if (policy == null)
            {
                log.Error("Invalid Policy for Cache {0}", this.name);
                throw new ArgumentNullException(nameof(policy));
            }

            if (!string.IsNullOrEmpty(policy.ConnectionName))
            {
                this.connectionString = CacheManager.GetConnectionString(policy.ConnectionName)?.ConnectionString;

                if (string.IsNullOrEmpty(connectionString))
                {
                    throw new ArgumentException(
                        $"{nameof(ICacheConnectionString.ConnectionString)} not found for {nameof(policy.ConnectionName)} {policy.ConnectionName}", $"{nameof(policy)}.{nameof(policy.ConnectionName)}");
                }
            }
            else if (!string.IsNullOrEmpty(policy.ConnectionString))
            {
                this.connectionString = policy.ConnectionString;
            }
            else
            {
                throw new ArgumentException(
                    $"{nameof(policy.ConnectionString)} is undefined", $"{nameof(policy)}.{nameof(policy.ConnectionString)}");
            }

            this.sender = Guid.NewGuid().ToString();
            this.convert = RedisConverterFactory.CreateConverter(policy.Converter);
        }

        public string Name { get { return this.name; } }

        private RedisClient GetSubClient(string cacheName, Func<CacheItemNotification, bool> callback)
        {
            if (callback != null)
                this.cacheCallbacks.AddOrUpdate(cacheName, callback, (k, c) => callback);

            var client = this.cacheSubClients.GetOrAdd(cacheName, cn => CreateClient());
            return client;
        }

        private RedisClient CreateClient()
        {
            var client = new RedisClient(
                this.connectionString, this.policy.ClusterType, this.policy.MonitorPort,
                this.policy.MonitorIntervalMilliseconds);
            return client;
        }

        // ReSharper disable once ParameterHidesMember
        public void Subscribe(string cacheName, Func<CacheItemNotification, bool> callback)
        {
            // Subscribe to Redis
            GetSubClient(cacheName, callback).Subscriber.Subscribe(cacheName, (channel, message) =>
            {
                var notificationInfo = convert.FromRedis(message);
                OnCacheUpdated(notificationInfo);
            });
        }

        public void UnSubscribe(string cacheName)
        {
            this.cacheCallbacks.TryRemove(cacheName, out Func<CacheItemNotification, bool> callback);
            
            // Unsubscribe from Redis
            GetSubClient(cacheName, null).Subscriber.Unsubscribe(cacheName, null, CommandFlags.None);
        }

        public void Publish(string cacheName, string key, CacheItemActionTypes action)
        {
            var message = new CacheItemNotification(sender, cacheName, key, action);
            var messageToSend = convert.ToRedis(message);
            GetSubClient(cacheName, null).Subscriber.Publish(cacheName, messageToSend, CommandFlags.None);
        }

        private void OnCacheUpdated(CacheItemNotification notification)
        {
            if (notification.CacheName == null)
                return;

            // Ignore own messages - prevent loops
            if (sender == notification.Sender)
            {
                return;
            }

            if (this.cacheCallbacks.TryGetValue(
                notification.CacheName, out Func<CacheItemNotification, bool> callback))
            {
                log.Debug($"Received {nameof(CacheItemNotification)} for cache={notification.CacheName}, key={notification.Key}");

                // CacheSynchronizer callback
                callback(notification);
            }
        }

        public void Dispose()
        {
            var subClients = this.cacheSubClients.Values.ToList();
            this.cacheSubClients = new ConcurrentDictionary<string, RedisClient>();

            foreach (var redisClient in subClients)
            {
                redisClient.Dispose();
            }

            this.cacheCallbacks = new ConcurrentDictionary<string, Func<CacheItemNotification, bool>>();
        }
    }
}