using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Configuration;
using System.Configuration.Provider;
using System.IO;
using System.Linq.Expressions;
using System.Web;
using System.Web.Configuration;
using System.Web.Hosting;
using System.Web.SessionState;

using MongoDB.Web.Config;
using MongoDB.Web.Common;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.Driver.Builders;

using CommonCore.Cache;

namespace MongoDB.Web.Providers
{
	public interface ISessionStateData
	{
		string ApplicationVirtualPath { get; set; }
		string SessionId { get; set; }

		DateTime Created { get; set; }
		DateTime Accessed { get; set; }
		DateTime Expires { get; set; }
		DateTime LockDate { get; set; }
		bool Locked { get; set; }
		string LockId { get; set; }
		SessionStateActions SessionStateActions { get; set; }
		byte[] SessionStateItems { get; set; }
		int SessionStateItemsCount { get; set; }
		int Timeout { get; set; }
	}

	public class DefaultSessionStateData : ISessionStateData
	{
		public class UniqueIdentifier
		{
			public string ApplicationVirtualPath { get; set; }
			public string SessionId { get; set; }
		}

		public DefaultSessionStateData()
		{
			Id = BsonObjectId.GenerateNewId();
		}

		[BsonId]
		public BsonObjectId Id { get; set; }

		[BsonElement("virtualPath")]
		public string ApplicationVirtualPath { get; set; }

		[BsonElement("sessionId")]
		public string SessionId { get; set; }

		[BsonElement("created")]
		public DateTime Created { get; set; }

		[BsonElement("accessed")]
		public DateTime Accessed { get; set; }

		[BsonElement("expires")]
		public DateTime Expires { get; set; }

		[BsonElement("lockDate")]
		public DateTime LockDate { get; set; }

		[BsonElement("locked")]
		public bool Locked { get; set; }

		[BsonElement("lockId")]
		public string LockId { get; set; }

		[BsonElement("actions")]
		public SessionStateActions SessionStateActions { get; set; }

		[BsonElement("items")]
		public byte[] SessionStateItems { get; set; }

		[BsonElement("itemsCount")]
		public int SessionStateItemsCount { get; set; }

		[BsonElement("timeout")]
		public int Timeout { get; set; }
	}

	public class MongoSessionStateProvider<T> : SessionStateStoreProviderBase
		where T : class, ISessionStateData, new()
    {
		private MongoCollection<T> _MongoCollection;
        private SessionStateSection _SessionStateSection;
		private MongoDbWebSection _MongoWebSection;
		private BsonClassMap<T> _SessionDataClassMap;
		private static Cache<string, T> _Cache;
		private static object _CacheGuarantee = new object();
		private static MemberHelper<T> _MemberHelper = new MemberHelper<T>();

		private string _IdField;
		private string _ApplicationVirtualPathField;
		private string _LockIdField;
		private string _LockedField;
		private string _ExpiresField;
		private string _AccessedField;
		private string _LockDateField;
		private string _SessionStateActionsField;
		private string _SessionItemsField;
		private SafeMode _SafeMode = null;
		private bool _UseLock = true;

        public override SessionStateStoreData CreateNewStoreData(HttpContext context, int timeout)
        {
            return new SessionStateStoreData(new SessionStateItemCollection(), SessionStateUtility.GetSessionStaticObjects(context), timeout);
        }

        public override void CreateUninitializedItem(HttpContext context, string id, int timeout)
        {
			var now = DateTime.UtcNow;

			var sessionData = new T()
			{
				SessionId = id,
				ApplicationVirtualPath = HostingEnvironment.ApplicationVirtualPath,
				Created = now,
				Accessed = now,
				Expires = now.AddMinutes(timeout),
				LockDate = now,
				Locked = false,
				LockId = Guid.NewGuid().ToString("N"),
				SessionStateActions = SessionStateActions.InitializeItem,
				SessionStateItems = new byte[0],
				SessionStateItemsCount = 0,
				Timeout = timeout
			};

			var result = this._MongoCollection.Insert(sessionData, _SafeMode);
			if (! result.Ok)
				throw new Exception(result.ErrorMessage);

			_Cache[id] = sessionData;
        }

        public override void Dispose()
        {
        }

        public override void EndRequest(HttpContext context)
        {
        }

        public override SessionStateStoreData GetItem(HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actions)
        {
            return GetSessionStateStoreData(false, context, id, out locked, out lockAge, out lockId, out actions);
        }

        public override SessionStateStoreData GetItemExclusive(HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actions)
        {
            return GetSessionStateStoreData(true, context, id, out locked, out lockAge, out lockId, out actions);
        }

        public override void Initialize(string name, NameValueCollection config)
        {
            this._SessionStateSection = ConfigurationManager.GetSection("system.web/sessionState") as SessionStateSection;
			this._MongoWebSection = ConfigurationManager.GetSection("mongoDbWeb") as MongoDbWebSection;

			this._MongoCollection = MongoServer.Create(ConnectionHelper.GetDatabaseConnectionString(_MongoWebSection, config))
				.GetDatabase(ConnectionHelper.GetDatabaseName(_MongoWebSection, config))
				.GetCollection<T>(config["collection"] ?? _MongoWebSection.SessionState.MongoCollectionName);
			_UseLock = config["useLock"] == "true" ? true : _MongoWebSection.SessionState.UseLock;

			_SessionDataClassMap = new BsonClassMap<T>();
			_SessionDataClassMap.AutoMap();

			_ApplicationVirtualPathField = MapBsonMember(t => t.ApplicationVirtualPath);
			_IdField = MapBsonMember(t => t.SessionId);
			_LockIdField = MapBsonMember(t => t.LockId);
			_LockedField = MapBsonMember(t => t.Locked);
			_AccessedField = MapBsonMember(t => t.Accessed);
			_ExpiresField = MapBsonMember(t => t.Expires);
			_LockDateField = MapBsonMember(t => t.LockDate);
			_SessionStateActionsField = MapBsonMember(t => t.SessionStateActions);
			_SessionItemsField = MapBsonMember(t => t.SessionStateItems);

			// Ideally, we'd just use the object's bson id - however, serialization gets a bit wonky
			// when trying to have an Id property which isn't a simple type in the base class
			// This also provides better backwards compatibility with the old BsonDocument implementation (field names match)
			this._MongoCollection.EnsureIndex(
				IndexKeys.Ascending(_ApplicationVirtualPathField, _IdField), IndexOptions.SetUnique(true));

			// MongoDB TTL collection http://docs.mongodb.org/manual/tutorial/expire-data/
			this._MongoCollection.EnsureIndex(IndexKeys.Ascending(_AccessedField), IndexOptions.SetTimeToLive(_SessionStateSection.Timeout));

			if (_Cache == null)
			{
				lock (_CacheGuarantee)
				{
					if (_Cache == null)
					{
						_Cache = new Cache<string, T>.Builder()
						{
							EntryExpiration = new TimeSpan(0, 0, _MongoWebSection.SessionState.MemoryCacheExpireSeconds),
							MaxEntries = _MongoWebSection.SessionState.MaxInMemoryCachedSessions
						}.Cache;
					}
				}
			}

			// Initialise safe mode options. Defaults to Safe Mode=true, fsynch=false, w=0 (replicas to write to before returning)
			bool safeModeEnabled = true;

			bool fsync = _MongoWebSection.FSync;
			if (config["fsync"] != null)
				bool.TryParse(config["fsync"], out fsync);

			int replicasToWrite = _MongoWebSection.ReplicasToWrite;
			if ((config["replicasToWrite"] != null) && (!int.TryParse(config["replicasToWrite"], out replicasToWrite)))
				throw new ProviderException("replicasToWrite must be a valid integer");

			_SafeMode = SafeMode.Create(safeModeEnabled, fsync, replicasToWrite);

            base.Initialize(name, config);
        }

        public override void InitializeRequest(HttpContext context)
        {
        }

        public override void ReleaseItemExclusive(HttpContext context, string id, object lockId)
        {
			var query = LookupQuery(id, lockId);

			var now = DateTime.UtcNow;

			var newAccessed = now;
			var newExpires = now.Add(_SessionStateSection.Timeout);

			var update = Update
				.Set(_AccessedField, newAccessed)
				.Set(_ExpiresField, newExpires)
				.Set(_LockedField, false);

			T session;
			_Cache.TryGetValue(id, out session);
			lock (session ?? new object())
			{
				var result = _MongoCollection.Update(query, update, _SafeMode);
				if ((result.DocumentsAffected != 0) && (session != null))
				{
					// update the cache if mongo was updated
					if ((lockId != null) && (session.LockId != (string)lockId))
						throw new InvalidDataException(String.Format("Cache out of sync with Mongo. Expected {0}, was {1}", lockId, session.LockId));

					session.Accessed = newAccessed;
					session.Expires = newExpires;
					session.Locked = false;
				}
			}
        }

        public override void RemoveItem(HttpContext context, string id, object lockId, SessionStateStoreData item)
        {
			this._MongoCollection.Remove(LookupQuery(id, lockId), _SafeMode);
			// We don't care about consistency - even if the lockId doesn't match, the cache will simply be cleared & reloaded
			_Cache.Remove(id);
        }

        public override void ResetItemTimeout(HttpContext context, string id)
        {
			var query = LookupQuery(id);

			var now = DateTime.UtcNow;

			var newAccessed = now;
			var newExpires = now.Add(_SessionStateSection.Timeout);

			var update = Update
				.Set(_AccessedField, newAccessed)
				.Set(_ExpiresField, newExpires);

			_MongoCollection.Update(query, update, _SafeMode);

			T session;
			if (_Cache.TryGetValue(id, out session))
			{
				lock (session)
				{
					session.Accessed = newAccessed;
					session.Expires = newExpires;
				}
			}
        }

        public override void SetAndReleaseItemExclusive(HttpContext context, string id, SessionStateStoreData item, object lockId, bool newItem)
        {
            using (var memoryStream = new MemoryStream())
            {
                using (var binaryWriter = new BinaryWriter(memoryStream))
                {
                    ((SessionStateItemCollection)item.Items).Serialize(binaryWriter);

					var now = DateTime.UtcNow;

					if (newItem)
					{
						var sessionData = new T()
						{
							SessionId = id,
							ApplicationVirtualPath = HostingEnvironment.ApplicationVirtualPath,
							Created = now,
							Accessed = now,
							Expires = now.AddMinutes(item.Timeout),
							LockDate = now,
							Locked = false,
							LockId = Guid.NewGuid().ToString("N"),
							SessionStateActions = SessionStateActions.None,
							SessionStateItems = memoryStream.ToArray(),
							SessionStateItemsCount = item.Items.Count,
							Timeout = item.Timeout
						};

						this._MongoCollection.Save(sessionData, _SafeMode);
						_Cache[id] = sessionData;
					}
					else
					{
						var sessionItems = memoryStream.ToArray();
						var accessed = now;
						var expiration = now.AddMinutes(item.Timeout);
						
						var update = Update
							.Set(_AccessedField, accessed)
							.Set(_ExpiresField, expiration)
							.Set(_LockedField, false)
							.Set(_SessionItemsField, sessionItems);

						T session;
						_Cache.TryGetValue(id, out session);
						lock (session ?? new object())
						{
							var result = _MongoCollection.Update(LookupQuery(id, lockId), update, _SafeMode);
							if ((result.DocumentsAffected != 0) && (session != null))
							{
								// Update the cache if mongo was updated
								if ((lockId != null) && (session.LockId != (string)lockId))
									throw new InvalidDataException(String.Format("Cache out of sync with Mongo. Expected {0}, was {1}", lockId, session.LockId));

								session.SessionStateItems = sessionItems;
								session.SessionStateItemsCount = item.Items.Count;
								session.Accessed = accessed;
								session.Expires = expiration;
							}
						}
					}
                }
            }
        }

        public override bool SetItemExpireCallback(SessionStateItemExpireCallback expireCallback)
        {
            return false;
        }

        #region Private Methods

        private SessionStateStoreData GetSessionStateStoreData(bool exclusive, HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actions)
		{
			// Use the same timestamp for all operations, for consistency
			var now = DateTime.UtcNow;

            actions = SessionStateActions.None;
            lockAge = TimeSpan.Zero;

			var lookupQuery = LookupQuery(id);
			T session;
			if (!_Cache.TryGetValue(id, out session))
			{
				session = _MongoCollection.FindOneAs<T>(lookupQuery);
				if (session != null)
				{
					// We need to make sure that this method has a reference to the instance in the cache,
					// in case there's a race condition when the session is loaded from mongo
					_Cache.GetOrAdd(id, session, out session);
				}
			}

			if (session != null)
			{
				if (session.Expires <= now)
				{
					_MongoCollection.Remove(lookupQuery, _SafeMode);
					_Cache.Remove(new KeyValuePair<string, T>(session.SessionId, session));
					session = null;
				}
				else if (session.Locked)
					lockAge = now.Subtract(session.LockDate);
				else
					actions = session.SessionStateActions;
			}

			locked = (session != null) && session.Locked;
			lockId = (session != null) ? (object)session.LockId : null;

			if (!_UseLock && !exclusive)
			{
				locked = false;
				lockId = null;
				lockAge = TimeSpan.Zero;
				actions = SessionStateActions.None;
			}
			else if (!_UseLock && exclusive && session != null)
			{
				locked = true;
			}

			if (exclusive && session != null)
            {
				var updatedActions = actions = SessionStateActions.None;
				var newLockId = Guid.NewGuid().ToString("N");

				var updateQuery = Query.And(
					Query.EQ(_ApplicationVirtualPathField, HostingEnvironment.ApplicationVirtualPath),
					Query.EQ(_IdField, id),
					Query.EQ(_LockedField, false),
					Query.GT(_ExpiresField, now));

				var update = Update.Set(_LockDateField, now)
					.Set(_LockIdField, newLockId)
					.Set(_LockedField, true)
					.Set(_SessionStateActionsField, updatedActions);

				lock (session)
				{
					var result = _MongoCollection.Update(updateQuery, update, _SafeMode);
					bool acquiredLock = result.DocumentsAffected != 0;

					if (acquiredLock)
					{
						lockId = newLockId;
						actions = updatedActions;

						session.LockDate = now;
						session.LockId = newLockId;
						session.Locked = true;
						session.SessionStateActions = updatedActions;
					}
					else
						locked = true;
				}
            }

            if (actions == SessionStateActions.InitializeItem)
                return CreateNewStoreData(context, _SessionStateSection.Timeout.Minutes);
			else if (session == null)
				return null;

            using (var memoryStream = new MemoryStream(session.SessionStateItems ?? new byte[0]))
            {
                var sessionStateItems = new SessionStateItemCollection();

                if (memoryStream.Length > 0)
                    sessionStateItems = SessionStateItemCollection.Deserialize(new BinaryReader(memoryStream));

                return new SessionStateStoreData(sessionStateItems, SessionStateUtility.GetSessionStaticObjects(context), session.Timeout);
            }
        }

		private IMongoQuery LookupQuery (string id)
		{
			return Query.And(
				Query.EQ(_ApplicationVirtualPathField, HostingEnvironment.ApplicationVirtualPath), 
				Query.EQ(_IdField, id));
		}

		private IMongoQuery LookupQuery(string id, object lockId)
		{
			var lockIdString = lockId as string;
			return Query.And(
				Query.EQ(_ApplicationVirtualPathField, HostingEnvironment.ApplicationVirtualPath),
				Query.EQ(_IdField, id),
				Query.EQ(_LockIdField, lockIdString));
		}

		private string MapBsonMember<TReturn>(Expression<Func<T, TReturn>> expression)
		{
			return _SessionDataClassMap.MapMember(_MemberHelper.GetMember(expression)).ElementName;
		}
        #endregion
    }

	public class MongoDBSessionStateProvider : MongoSessionStateProvider<DefaultSessionStateData>
	{
	}
}