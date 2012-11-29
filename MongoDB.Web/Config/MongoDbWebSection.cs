using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;

using CommonCore.Configuration;

namespace MongoDB.Web.Config
{
	class MongoDbWebSection : AutoConfigurationSection
	{
		[ConfigurationProperty("connectionString", DefaultValue = "mongodb://localhost")]
		public string ConnectionString { get; set; }
		[ConfigurationProperty("databaseName", DefaultValue = "ASPNETDB")]
		public string DatabaseName { get; set; }

		[ConfigurationProperty("fsync", DefaultValue = false)]
		public bool FSync { get; set; }
		[ConfigurationProperty("replicasToWrite", DefaultValue = 0)]
		public int ReplicasToWrite { get; set; }

		[ConfigurationProperty("sessionState")]
		public MongoDbSessionStateSection SessionState { get; set; }
	}

	class MongoDbSessionStateSection : AutoConfigurationElement
	{
		[ConfigurationProperty("mongoCollectionName", DefaultValue = "SessionState")]
		public string MongoCollectionName { get; set; }
		[ConfigurationProperty("memoryCacheExpireSeconds", DefaultValue = 100)]
		public int MemoryCacheExpireSeconds { get; set; }
		[ConfigurationProperty("maxInMemoryCachedSessions", DefaultValue = 10000L)]
		public long MaxInMemoryCachedSessions { get; set; }
		[ConfigurationProperty("useLock", DefaultValue = true)]
		public bool UseLock { get; set; }
	}
}
