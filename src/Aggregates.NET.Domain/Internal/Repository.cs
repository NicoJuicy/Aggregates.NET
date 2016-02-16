﻿using Aggregates.Contracts;
using Aggregates.Exceptions;
using NServiceBus;
using NServiceBus.Logging;
using NServiceBus.ObjectBuilder;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Aggregates.Internal
{
    // inspired / taken from NEventStore.CommonDomain
    // https://github.com/NEventStore/NEventStore/blob/master/src/NEventStore/CommonDomain/Persistence/EventStore/EventStoreRepository.cs

    public class Repository<T> : IRepository<T> where T : class, IEntity
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(Repository<>));
        private readonly IStoreEvents _store;
        private readonly IStoreSnapshots _snapstore;
        private readonly IBuilder _builder;

        private readonly ConcurrentDictionary<String, ISnapshot> _snapshots = new ConcurrentDictionary<String, ISnapshot>();
        private readonly ConcurrentDictionary<String, IEventStream> _streams = new ConcurrentDictionary<String, IEventStream>();
        private Boolean _disposed;

        public Repository(IBuilder builder)
        {
            _builder = builder;
            _store = _builder.Build<IStoreEvents>();
            _snapstore = _builder.Build<IStoreSnapshots>();
        }
        ~Repository()
        {
            Dispose(false);
        }

        void IRepository.Commit(Guid commitId, IDictionary<String, Object> headers)
        {
            foreach (var stream in _streams)
                stream.Value.Commit(commitId, headers);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed || !disposing)
                return;

            _snapshots.Clear();
            _streams.Clear();

            _disposed = true;
        }

        public virtual T Get<TId>(TId id)
        {
            return Get<TId>(Bucket.Default, id);
        }

        public T Get<TId>(String bucket, TId id)
        {
            Logger.DebugFormat("Retreiving aggregate id '{0}' from bucket '{1}' in store", id, bucket);

            var snapshot = GetSnapshot(bucket, id);
            var stream = OpenStream(bucket, id, snapshot);

            if (stream == null && snapshot == null)
                throw new NotFoundException("Aggregate snapshot not found");

            // Get requires the stream exists
            if (stream.StreamVersion == -1)
                throw new NotFoundException("Aggregate stream not found");

            // Call the 'private' constructor
            var root = Newup(stream, _builder);
            (root as IEventSource<TId>).Id = id;

            if (snapshot != null && root is ISnapshotting)
                ((ISnapshotting)root).RestoreSnapshot(snapshot.Payload);

            root.Hydrate(stream.Events.Select(e => e.Event));

            return root;
        }

        public virtual T New<TId>(TId id)
        {
            return New<TId>(Bucket.Default, id);
        }

        public T New<TId>(String bucket, TId id)
        {
            var stream = PrepareStream(bucket, id);
            var root = Newup(stream, _builder);
            (root as IEventSource<TId>).Id = id;

            return root;
        }

        protected T Newup(IEventStream stream, IBuilder builder)
        {
            // Call the 'private' constructor
            var tCtor = typeof(T).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { }, null);

            if (tCtor == null)
                throw new AggregateException("Aggregate needs a PRIVATE parameterless constructor");
            var root = (T)tCtor.Invoke(null);

            // Todo: I bet there is a way to make a INeedBuilding<T> type interface
            //      and loop over each, calling builder.build for each T
            if (root is INeedStream)
                (root as INeedStream).Stream = stream;
            if (root is INeedBuilder)
                (root as INeedBuilder).Builder = builder;
            if (root is INeedEventFactory)
                (root as INeedEventFactory).EventFactory = builder.Build<IMessageCreator>();
            if (root is INeedRouteResolver)
                (root as INeedRouteResolver).Resolver = builder.Build<IRouteResolver>();
            if (root is INeedRepositoryFactory)
                (root as INeedRepositoryFactory).RepositoryFactory = builder.Build<IRepositoryFactory>();
            if (root is INeedMutator)
                (root as INeedMutator).Mutator = builder.Build<IEventMutator>();

            return root;
        }

        public virtual IEnumerable<T> Query<TMemento, TId>(Expression<Func<TMemento, Boolean>> predicate) where TMemento : class, IMemento<TId>
        {
            return Query<TMemento, TId>(Bucket.Default, predicate);
        }
        public IEnumerable<T> Query<TMemento, TId>(String bucket, Expression<Func<TMemento, Boolean>> predicate) where TMemento : class, IMemento<TId>
        {
            // Queries the snapshot store for the user's predicate and returns matching entities
            return _snapstore.Query<T, TId, TMemento>(bucket, predicate).Select(x =>
            {
                var memento = x.Payload as TMemento;
                var stream = OpenStream(bucket, x.Stream, x);
                
                // Call the 'private' constructor
                var entity = Newup(stream, _builder);
                (entity as IEventSource<TId>).Id = memento.EntityId;

                ((ISnapshotting)entity).RestoreSnapshot(memento);

                entity.Hydrate(stream.Events.Select(e => e.Event));

                return entity;
            }).ToList();
        }

        private ISnapshot GetSnapshot<TId>(String bucket, TId id)
        {
            ISnapshot snapshot;
            var snapshotId = String.Format("{1}-{0}-{2}", typeof(T).FullName, bucket, id);
            if (!_snapshots.TryGetValue(snapshotId, out snapshot))
            {
                _snapshots[snapshotId] = snapshot = _snapstore.GetSnapshot(bucket, id.ToString());
            }

            return snapshot;
        }

        private IEventStream OpenStream<TId>(String bucket, TId id, ISnapshot snapshot)
        {
            return OpenStream(bucket, id.ToString(), snapshot);
        }
        private IEventStream OpenStream(String bucket, String streamId, ISnapshot snapshot)
        {
            var cacheId = String.Format("{0}-{1}", bucket, streamId);
            IEventStream cached;
            if (_streams.TryGetValue(cacheId, out cached))
                return cached;
            
            if (snapshot == null)
                _streams[cacheId] = cached = _store.GetStream<T>(bucket, streamId);
            else
                _streams[cacheId] = cached = _store.GetStream<T>(bucket, streamId, snapshot.Version + 1);
            return cached;
        }

        private IEventStream PrepareStream<TId>(String bucket, TId id)
        {
            IEventStream cached;
            var cacheId = String.Format("{0}-{1}", bucket, id);
            if (_streams.TryGetValue(cacheId, out cached))
                return cached;

            _streams[cacheId] = cached = _store.GetStream<T>(bucket, id.ToString());

            return cached;
        }
    }
}