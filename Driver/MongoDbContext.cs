﻿using EasyMongoNet.Model;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;

namespace EasyMongoNet
{
    /// <summary>
    /// Main implementation of IDbContext. Use this class as your main definition of IDbContext.
    /// </summary>
    public class MongoDbContext : IDbContext
    {
        private readonly IObjectSavePreprocessor objectPreprocessor;
        private readonly bool setDictionaryConventionToArrayOfDocuments;
        private readonly List<CustomMongoConnection> customConnections = new List<CustomMongoConnection>();
        private readonly Dictionary<string, object> Collections = new Dictionary<string, object>();
        private readonly string dbName;

        private static readonly Dictionary<Type, CollectionSaveAttribute> SaveAttrsCache = new Dictionary<Type, CollectionSaveAttribute>();
        public readonly IMongoDatabase Database;
        public bool DefaultWriteLog { get; set; } = false;
        public bool DefaultPreprocess { get; set; } = false;
        public Func<string> GetUserNameFunc { get; set; }

        public MongoDbContext(string dbName, MongoClientSettings mongoClientSettings, 
            Func<string> getUsernameFunc = null, bool setDictionaryConventionToArrayOfDocuments = false, 
            IEnumerable<CustomMongoConnection> customConnections = null, IObjectSavePreprocessor objectPreprocessor = null)
        {
            this.dbName = dbName;
            this.GetUserNameFunc = getUsernameFunc;
            this.setDictionaryConventionToArrayOfDocuments = setDictionaryConventionToArrayOfDocuments;
            this.Database = GetDatabase(mongoClientSettings, dbName, setDictionaryConventionToArrayOfDocuments);
            if (customConnections != null)
                this.customConnections.AddRange(customConnections);
            this.objectPreprocessor = objectPreprocessor;
        }

        public MongoDbContext(string dbName, string connectionString,
            Func<string> getUsernameFunc = null, bool setDictionaryConventionToArrayOfDocuments = false,
            IEnumerable<CustomMongoConnection> customConnections = null, IObjectSavePreprocessor objectPreprocessor = null)
            : this(dbName, MongoClientSettings.FromConnectionString(connectionString), 
                getUsernameFunc, setDictionaryConventionToArrayOfDocuments, customConnections, objectPreprocessor)
        { }

        private static IMongoDatabase GetDatabase(MongoClientSettings mongoClientSettings, string dbName, bool setDictionaryConventionToArrayOfDocuments)
        {
            MongoClient client = new MongoClient(mongoClientSettings);
            var db = client.GetDatabase(dbName);

            if (setDictionaryConventionToArrayOfDocuments)
            {
                ConventionRegistry.Register(
                    nameof(DictionaryRepresentationConvention),
                    new ConventionPack { new DictionaryRepresentationConvention(DictionaryRepresentation.ArrayOfDocuments) }, _ => true);
            }
            BsonSerializer.RegisterSerializationProvider(new CustomSerializationProvider());
            return db;
        }

        protected IMongoCollection<T> GetCollection<T>()
        {
            string collectionName = GetCollectionName(typeof(T));
            if (Collections.ContainsKey(collectionName))
                return (IMongoCollection<T>)Collections[collectionName];

            IMongoCollection<T> collection;
            var conn = customConnections.FirstOrDefault(c => c.Type == collectionName);
            if (conn != null)
                collection = GetCollection<T>(GetDatabase(conn.ConnectionSettings, conn.DBName, setDictionaryConventionToArrayOfDocuments));
            else
                collection = GetCollection<T>(Database);

            SetIndexes(collection);
            try
            {
                Collections.Add(collectionName, collection);
            }
            catch { }
            return collection;
        }

        private static IMongoCollection<T> GetCollection<T>(IMongoDatabase db)
        {
            CollectionOptionsAttribute attr = (CollectionOptionsAttribute)Attribute.GetCustomAttribute(typeof(T), typeof(CollectionOptionsAttribute));
            string collectionName = attr?.Name ?? typeof(T).Name;
            if (attr != null && !CheckCollectionExists(db, collectionName))
            {
                CreateCollectionOptions options = new CreateCollectionOptions();
                if (attr.Capped)
                {
                    options.Capped = attr.Capped;
                    if (attr.MaxSize > 0)
                        options.MaxSize = attr.MaxSize;
                    if (attr.MaxDocuments > 0)
                        options.MaxDocuments = attr.MaxDocuments;
                }
                db.CreateCollection(collectionName, options);
            }
            return db.GetCollection<T>(collectionName);
        }

        private static string GetCollectionName(Type type)
        {
            var attrs = (CollectionOptionsAttribute[])type.GetCustomAttributes(typeof(CollectionOptionsAttribute), true);
            if (attrs == null || attrs.Length == 0 || attrs[0].Name == null)
                return type.Name;
            return attrs[0].Name;
        }

        private static bool CheckCollectionExists(IMongoDatabase db, string collectionName)
        {
            var filter = new BsonDocument("name", collectionName);
            var collectionCursor = db.ListCollections(new ListCollectionsOptions { Filter = filter });
            return collectionCursor.Any();
        }

        private static void SetIndexes<T>(IMongoCollection<T> collection)
        {
            foreach (CollectionIndexAttribute attr in typeof(T).GetCustomAttributes<CollectionIndexAttribute>())
            {
                var options = new CreateIndexOptions { Sparse = attr.Sparse, Unique = attr.Unique };
                if (attr.ExpireAfterSeconds > 0)
                    options.ExpireAfter = new TimeSpan(attr.ExpireAfterSeconds * 10000000);
                CreateIndexModel<T> model = new CreateIndexModel<T>(GetIndexKeysDefinition<T>(attr), options);
                collection.Indexes.CreateOne(model);
            }
        }

        private static IndexKeysDefinition<T> GetIndexKeysDefinition<T>(CollectionIndexAttribute attr)
        {
            if (attr.Fields.Length == 1)
                return GetIndexDefForOne<T>(attr.Fields[0], attr.Types != null && attr.Types.Length > 0 ? attr.Types[0] : MongoIndexType.Ascending);

            List<IndexKeysDefinition<T>> list = new List<IndexKeysDefinition<T>>(attr.Fields.Length);
            for (int i = 0; i < attr.Fields.Length; i++)
                list.Add(GetIndexDefForOne<T>(attr.Fields[i], attr.Types != null && attr.Fields.Length > i ? attr.Types[i] : MongoIndexType.Ascending));
            return Builders<T>.IndexKeys.Combine(list);
        }

        private static IndexKeysDefinition<T> GetIndexDefForOne<T>(string field, MongoIndexType type)
        {
            switch (type)
            {
                case MongoIndexType.Ascending:
                    return Builders<T>.IndexKeys.Ascending(field);
                case MongoIndexType.Descending:
                    return Builders<T>.IndexKeys.Descending(field);
                case MongoIndexType.Geo2D:
                    return Builders<T>.IndexKeys.Geo2D(field);
                case MongoIndexType.Geo2DSphere:
                    return Builders<T>.IndexKeys.Geo2DSphere(field);
                case MongoIndexType.Text:
                    return Builders<T>.IndexKeys.Text(field);
                case MongoIndexType.Hashed:
                    return Builders<T>.IndexKeys.Hashed(field);
                default:
                    throw new Exception();
            }
        }

        private static CollectionSaveAttribute GetSaveAttribute(Type type)
        {
            if (SaveAttrsCache.ContainsKey(type))
                return SaveAttrsCache[type];
            var attr = (CollectionSaveAttribute)Attribute.GetCustomAttribute(type, typeof(CollectionSaveAttribute));
            if (attr != null)
                SaveAttrsCache.Add(type, attr);
            return attr;
        }

        private bool ShouldWriteLog<T>()
        {
            bool writeLog = DefaultWriteLog;
            CollectionSaveAttribute attr = GetSaveAttribute(typeof(T));
            if (attr != null)
                writeLog = attr.WriteLog;
            return writeLog;
        }

        public T FindById<T>(string id) where T : IMongoEntity
        {
            return GetCollection<T>().Find(t => t.Id == id).FirstOrDefault();
        }

        public T FindFirst<T>(Expression<Func<T, bool>> filter) where T : IMongoEntity
        {
            return Find(filter).FirstOrDefault();
        }

        public IEnumerable<T> FindGetResults<T>(Expression<Func<T, bool>> filter) where T : IMongoEntity
        {
            return Find(filter).ToEnumerable();
        }

        public void Save<T>(T item) where T : IMongoEntity
        {
            bool writeLog = DefaultWriteLog;
            bool preprocess = DefaultPreprocess;
            Type type = typeof(T);
            CollectionSaveAttribute attr = GetSaveAttribute(type);
            if (attr != null)
            {
                writeLog = attr.WriteLog;
                preprocess = attr.Preprocess;
            }

            if (preprocess && objectPreprocessor != null)
                objectPreprocessor.Preprocess(item);

            ActivityType activityType;
            T oldValue = default(T);
            if (item.Id == null)
            {
                GetCollection<T>().InsertOne(item);
                activityType = ActivityType.Insert;
            }
            else
            {
                if (writeLog)
                    oldValue = FindById<T>(item.Id);
                GetCollection<T>().ReplaceOne(t => t.Id == item.Id, item, new ReplaceOptions { IsUpsert = true });
                activityType = ActivityType.Update;
            }
            if (writeLog)
            {
                string username = GetUserNameFunc();
                if (activityType == ActivityType.Insert)
                {
                    var insertActivity = new InsertActivity(username) { CollectionName = GetCollectionName(type), ObjId = item.Id };
                    Save((UserActivity)insertActivity);
                }
                else
                {
                    UpdateActivity update = new UpdateActivity(username) { CollectionName = GetCollectionName(type), ObjId = oldValue.Id };
                    update.SetDiff(oldValue, item);
                    if (update.Diff.Count > 0)
                        Save((UserActivity)update);
                }
            }
        }

        public DeleteResult DeleteOne<T>(T item) where T : IMongoEntity
        {
            var result = GetCollection<T>().DeleteOne(t => t.Id == item.Id);
            if (ShouldWriteLog<T>())
            {
                var deleteActivity = new DeleteActivity(GetUserNameFunc()) { CollectionName = GetCollectionName(typeof(T)), ObjId = item.Id, DeletedObj = item };
                Save((UserActivity)deleteActivity);
            }
            return result;
        }

        public DeleteResult DeleteOne<T>(string id) where T : IMongoEntity
        {
            if (ShouldWriteLog<T>())
            {
                T item = FindById<T>(id);
                if (item != null)
                {
                    var deleteActivity = new DeleteActivity(GetUserNameFunc()) { CollectionName = GetCollectionName(typeof(T)), ObjId = item.Id, DeletedObj = item };
                    Save((UserActivity)deleteActivity);
                }
            }
            return GetCollection<T>().DeleteOne(t => t.Id == id);
        }

        public DeleteResult DeleteOne<T>(Expression<Func<T, bool>> filter) where T : IMongoEntity
        {
            if (ShouldWriteLog<T>())
            {
                T item = Find(filter).FirstOrDefault();
                if (item != null)
                {
                    var deleteActivity = new DeleteActivity(GetUserNameFunc()) { CollectionName = GetCollectionName(typeof(T)), ObjId = item.Id, DeletedObj = item };
                    Save((UserActivity)deleteActivity);
                }
            }
            return GetCollection<T>().DeleteOne(filter);
        }

        public DeleteResult DeleteMany<T>(Expression<Func<T, bool>> filter) where T : IMongoEntity
        {
            return GetCollection<T>().DeleteMany<T>(filter);
        }

        public UpdateResult UpdateOne<T>(Expression<Func<T, bool>> filter, UpdateDefinition<T> updateDef, UpdateOptions options = null) where T : IMongoEntity
        {
            T oldValue = default(T);
            bool writeLog = ShouldWriteLog<T>();
            if (writeLog)
                oldValue = Find(filter).FirstOrDefault();
            var res = GetCollection<T>().UpdateOne(filter, updateDef, options);
            if (oldValue != null)
            {
                UpdateActivity updateActivity = new UpdateActivity(GetUserNameFunc()) { CollectionName = GetCollectionName(typeof(T)), ObjId = oldValue.Id };
                T newValue = FindById<T>(oldValue.Id);
                updateActivity.SetDiff(oldValue, newValue);
                Save((UserActivity)updateActivity);
            }
            return res;
        }

        public UpdateResult UpdateMany<T>(FilterDefinition<T> filter, UpdateDefinition<T> update, UpdateOptions options = null) where T : IMongoEntity
        {
            return GetCollection<T>().UpdateMany(filter, update, options);
        }

        public UpdateResult UpdateMany<T>(Expression<Func<T, bool>> filter, UpdateDefinition<T> update, UpdateOptions options = null) where T : IMongoEntity
        {
            return GetCollection<T>().UpdateMany(filter, update, options);
        }

        public IEnumerable<T> All<T>() where T : IMongoEntity
        {
            return GetCollection<T>().Find(FilterDefinition<T>.Empty).ToEnumerable();
        }

        public bool Any<T>(Expression<Func<T, bool>> filter) where T : IMongoEntity
        {
            return GetCollection<T>().Find(filter).Project(t => t.Id).FirstOrDefault() != null;
        }

        public IFindFluent<T, T> Find<T>(Expression<Func<T, bool>> filter, FindOptions options = null) where T : IMongoEntity
        {
            return GetCollection<T>().Find(filter, options);
        }

        public IFindFluent<T, T> Find<T>(FilterDefinition<T> filter, FindOptions options = null) where T : IMongoEntity
        {
            return GetCollection<T>().Find(filter, options);
        }

        public long Count<T>() where T : IMongoEntity
        {
            return GetCollection<T>().CountDocuments(_ => true);
        }

        public long Count<T>(Expression<Func<T, bool>> filter, CountOptions options = null) where T : IMongoEntity
        {
            return GetCollection<T>().CountDocuments(filter, options);
        }

        public long Count<T>(FilterDefinition<T> filter, CountOptions options = null) where T : IMongoEntity
        {
            return GetCollection<T>().CountDocuments(filter, options);
        }

        public IAggregateFluent<T> Aggregate<T>(AggregateOptions options = null) where T : IMongoEntity
        {
            return GetCollection<T>().Aggregate(options);
        }

        public IMongoQueryable<T> AsQueryable<T>(AggregateOptions options = null) where T : IMongoEntity
        {
            return GetCollection<T>().AsQueryable(options);
        }

        public void InsertMany<T>(IEnumerable<T> items, InsertManyOptions options = null) where T : IMongoEntity
        {
            GetCollection<T>().InsertMany(items, options);
        }

        public Task InsertManyAsync<T>(IEnumerable<T> items, InsertManyOptions options = null) where T : IMongoEntity
        {
            return GetCollection<T>().InsertManyAsync(items, options);
        }
    }
}
