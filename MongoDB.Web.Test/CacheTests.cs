using System;
using System.Configuration;
using System.Reflection;
using System.Web;
using System.Web.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoDB.Web.Providers;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;

namespace MongoDB.Web.Test
{
	/// <summary>
	/// Verifies caching functionality for MongoDB
	/// </summary>
	[TestClass]
	public class CacheTests
	{
		static System.Web.SessionState.SessionIDManager _SessionIdManager;
		static SessionStateSection _SessionStateSection;
		static ProviderSettings _ProviderSettings;
		static int _TimeoutInSeconds;

		public TestContext TestContext { get; set; }

		#region Test Creation and Destruction

		/// <summary>
		/// Automatically invoked before executing any unit test
		/// </summary>
		/// <param name="testContext">Auto assigned</param>
		[ClassInitialize]
		public static void CacheTests_Initialize(TestContext testContext)
		{
			_SessionIdManager = new System.Web.SessionState.SessionIDManager();

			_SessionStateSection = (SessionStateSection)WebConfigurationManager.GetSection("system.web/sessionState");
			_ProviderSettings = (ProviderSettings)_SessionStateSection.Providers[_SessionStateSection.CustomProvider];
			_TimeoutInSeconds = (int)_SessionStateSection.Timeout.TotalSeconds;

			UpdateHostingEnvironment();
		}

		/// <summary>
		/// Automatically invoked when tests are completed
		/// </summary>
		[ClassCleanup]
		public static void CacheTests_Cleanup()
		{
			// TODO: Cleanup
		}

		/// <summary>
		/// Automatically invoked prior to a test starting
		/// </summary>
		[TestInitialize]
		public void CacheTests_TestInitialize()
		{
			// The Cache is a static member on the provider. We'll use reflection to null
			// it out so that the correct cache is used in the Initialize method.
			Type providerType = typeof(MongoDBSessionStateProvider).BaseType;
			providerType.GetField("_Cache", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).SetValue(null, null);
			providerType.GetField("_MemoryCache", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).SetValue(null, null);
		}

		#endregion

		#region Public Test Methods

		/// <summary>
		/// Tests the sliding expiration functionality of a MongoDB session object
		/// and then checks to make sure the expiration of the same object occurs
		/// against the CommonCore.Cache backing store.
		/// </summary>
		[TestMethod]
		public void VerifySessionSlidingExpirationWithCommonCoreCache()
		{
			var provider = GetMongoDBSessionStateProvider(false);

			try
			{
				VerifySessionSlidingExpiration(provider);
			}
			finally
			{
				provider.Dispose();
			}
		}

		/// <summary>
		/// Tests the sliding expiration functionality of a MongoDB session object
		/// and then checks to make sure the expiration of the same object occurs
		/// against the System.Runtime.Caching.MemoryCache backing store.
		/// </summary>
		[TestMethod]
		public void VerifySessionSlidingExpirationWithDotNetMemoryCache()
		{
			var provider = GetMongoDBSessionStateProvider(true);

			try
			{
				VerifySessionSlidingExpiration(provider);
			}
			finally
			{
				provider.Dispose();
			}
		}

		/// <summary>
		/// Tests that sessions can be created, modified, and deleted using a MongoDB
		/// session provider with a CommonCore.Cache backing store.
		/// </summary>
		[TestMethod]
		public void VerifySessionPersistenceWithCommonCoreCache()
		{
			var provider = GetMongoDBSessionStateProvider(false);

			try
			{
				VerifySessionPersistence(provider);
			}
			finally
			{
				provider.Dispose();
			}
		}

		/// <summary>
		/// Tests that sessions can be created, modified, and deleted using a MongoDB
		/// session provider with a System.Runtime.Caching.MemoryCache backing store.
		/// </summary>
		[TestMethod]
		public void VerifySessionPersistenceWithDotNetMemoryCache()
		{
			var provider = GetMongoDBSessionStateProvider(true);

			try
			{
				VerifySessionPersistence(provider);
			}
			finally
			{
				provider.Dispose();
			}
		}

		/// <summary>
		/// Tests that sessions can be created and then automatically expire using a MongoDB
		/// session provider with a CommonCore.Cache backing store.
		/// </summary>
		[TestMethod]
		public void VerifyExpirationWithCommonCoreCache()
		{
			var provider = GetMongoDBSessionStateProvider(false);

			try
			{
				VerifySessionExpiration(provider);
			}
			finally
			{
				provider.Dispose();
			}
		}

		/// <summary>
		/// Tests that sessions can be created and then automatically expire using a MongoDB
		/// session provider with a System.Runtime.Caching.MemoryCache backing store.
		/// </summary>
		[TestMethod]
		public void VerifyExpirationWithDotNetMemoryCache()
		{
			var provider = GetMongoDBSessionStateProvider(true);

			try
			{
				VerifySessionExpiration(provider);
			}
			finally
			{
				provider.Dispose();
			}
		}

		/// <summary>
		/// Creates 500 sessions each with 1MB of binary data using the MongoDB session provider
		/// and a CommonCore.Cache backing store.  This method will verify that all the sessions
		/// have expired, all sessions have been removed from the store and cache, and memory usage
		/// was not excessive.
		/// </summary>
		[TestMethod]
		public void CreateConcurrentSessionsToTestMemoryUsagePostExpirationWithCommonCoreCache()
		{
			var provider = GetMongoDBSessionStateProvider(false);

			try
			{
				// Creates 500 sessions each with 1MB of data
				CreateConcurrentSessionObjects(provider, 500, 1);
			}
			finally
			{
				provider.Dispose();
			}
		}

		/// <summary>
		/// Creates 500 sessions each with 1MB of binary data using the MongoDB session provider
		/// and a System.Runtime.Caching.MemoryCache backing store.  This method will verify that
		/// all the sessions have expired, all sessions have been removed from the store and cache,
		/// and memory usage was not excessive.
		/// </summary>
		[TestMethod]
		public void CreateConcurrentSessionsToTestMemoryUsagePostExpirationWithDotNetMemoryCache()
		{
			var provider = GetMongoDBSessionStateProvider(true);

			try
			{
				// Creates 500 sessions each with 1MB of data
				CreateConcurrentSessionObjects(provider, 500, 1);
			}
			finally
			{
				provider.Dispose();
			}
		}

		#endregion

		#region Logic

		/// <summary>
		/// Method that creates a new session and touches it right before the expiry a couple of times.
		/// The final time it will not wait until after the expiration which the method will then
		/// expect the session to no longer be available.
		/// </summary>
		/// <param name="provider">The MongoDB provider to use for the test.</param>
		private void VerifySessionSlidingExpiration(MongoDBSessionStateProvider provider)
		{
			HttpRequest request = null;
			HttpResponse response = null;
			HttpContext context = GetContext(out request, out response);

			var sessionId = _SessionIdManager.CreateSessionID(context);
			var dataStore = provider.CreateNewStoreData(context, (_TimeoutInSeconds / 60));
			dataStore.Items["Dummy"] = "Value";

			TestContext.WriteLine("Creating dummy session with id {0}", sessionId);

			provider.SetAndReleaseItemExclusive(context, sessionId, dataStore, null, true);

			int iterations = 4;
			for (int i = 0; i < iterations; i++)
			{
				bool isLastIteration = (i == iterations - 1);

				int counter = _TimeoutInSeconds + (isLastIteration ? 10 : -10);
				TestContext.WriteLine("Waiting {0} seconds (expiration set to {1} seconds)...", counter, _TimeoutInSeconds);

				while (counter > 0)
				{
					System.Threading.Thread.Sleep(1000);
					counter--;
				}

				TestContext.WriteLine("Retrieving session again to reset expiry");

				bool locked;
				TimeSpan lockAge;
				object lockId;
				System.Web.SessionState.SessionStateActions actions;

				var dataStore2 = provider.GetItemExclusive(context, sessionId, out locked, out lockAge, out lockId, out actions);
				if (isLastIteration)
				{
					if (dataStore2 != null)
					{
						Assert.Fail("Session has NOT expired.");
					}

					ISessionStateData dataFromCache;
					bool existsInCache = CheckSessionExistsInCache(provider, sessionId, out dataFromCache);

					if (existsInCache)
					{
						Assert.Fail("Session has expired however it still exists in the memory cache.");
					}
				}
				else
				{
					if (dataStore2 == null || dataStore2.Items.Count == 0)
					{
						Assert.Fail("Session Missing prior to expiry??");
					}
					else
					{
						TestContext.WriteLine("Session retrieved successfully during iteration {0}", (i + 1));
					}
				}

				if (lockId != null)
				{
					provider.ReleaseItemExclusive(context, sessionId, lockId);
				}
			}
		}

		/// <summary>
		/// Method that creates a new session using the provider supplied.  This method
		/// also verifies that the session was created, then modifies the session, then
		/// deletes the session and verifies deletion from the store and the memory cache
		/// in the provider.
		/// </summary>
		/// <param name="provider">The MongoDB provider to use for the test.</param>
		private void VerifySessionPersistence(MongoDBSessionStateProvider provider)
		{
			string itemName = "DummyItem";
			string itemValue = "Value";

			HttpRequest request = null;
			HttpResponse response = null;
			HttpContext context = GetContext(out request, out response);

			TestContext.WriteLine("Creating Store Data");
			var dataStore = provider.CreateNewStoreData(context, (_TimeoutInSeconds / 60));
			dataStore.Items[itemName] = itemValue;

			var sessionId = _SessionIdManager.CreateSessionID(context);

			TestContext.WriteLine("Writing Store Data");
			provider.SetAndReleaseItemExclusive(context, sessionId, dataStore, null, true);

			System.Threading.Thread.Sleep(2000);

			bool locked;
			TimeSpan lockAge;
			object lockId;
			System.Web.SessionState.SessionStateActions actions;

			TestContext.WriteLine("Retrieving new Store Data");
			var retrievedDataStore = provider.GetItemExclusive(context, sessionId, out locked, out lockAge, out lockId, out actions);

			if (retrievedDataStore == null || retrievedDataStore.Items.Count == 0)
			{
				Assert.Fail("Retrieved data store does not contain session data");
			}

			TestContext.WriteLine("Testing Store Data");
			var dummyValue = retrievedDataStore.Items[itemName];
			Assert.AreEqual(itemValue, dummyValue);

			itemValue = "NewValue";
			retrievedDataStore.Items[itemName] = itemValue;

			TestContext.WriteLine("Updating Store Data");
			provider.SetAndReleaseItemExclusive(context, sessionId, retrievedDataStore, lockId, false);

			System.Threading.Thread.Sleep(2000);

			var retrievedDataStore2 = provider.GetItemExclusive(context, sessionId, out locked, out lockAge, out lockId, out actions);
			if (retrievedDataStore2 == null || retrievedDataStore2.Items.Count == 0)
			{
				Assert.Fail("Retrieved data store does not contain session data");
			}

			TestContext.WriteLine("Testing Store Data");
			Assert.AreEqual(itemValue, retrievedDataStore2.Items[itemName]);

			TestContext.WriteLine("Releasing Store Data");
			provider.ReleaseItemExclusive(context, sessionId, lockId);

			System.Threading.Thread.Sleep(2000);

			retrievedDataStore2 = provider.GetItemExclusive(context, sessionId, out locked, out lockAge, out lockId, out actions);
			TestContext.WriteLine("Deleting Store Data");
			provider.RemoveItem(context, sessionId, lockId, retrievedDataStore2);

			System.Threading.Thread.Sleep(2000);

			TestContext.WriteLine("Ensuring store was deleted");
			var retrievedDataStore3 = provider.GetItem(context, sessionId, out locked, out lockAge, out lockId, out actions);
			Assert.IsNull(retrievedDataStore3);

			System.Threading.Thread.Sleep(2000);

			TestContext.WriteLine("Ensuring cache is empty");
			ISessionStateData sessionStateDataFromCache = null;
			bool sessionExistsInCache = CheckSessionExistsInCache(provider, sessionId, out sessionStateDataFromCache);
			if (sessionExistsInCache)
			{
				Assert.Fail("Session should have been removed but still exists in cache");
			}

			TestContext.WriteLine("Success");
		}

		/// <summary>
		/// Method that creates a new session, waits for it to automatically expire and then
		/// verifies that the session has been removed from the provider as well as the provider's
		/// memory cache.
		/// </summary>
		/// <param name="mongoSessionStateProvider">The MongoDB provider to use for the test.</param>
		private void VerifySessionExpiration(MongoDBSessionStateProvider mongoSessionStateProvider)
		{
			var mongoSessionStateProviderBaseType = mongoSessionStateProvider.GetType().BaseType;

			HttpRequest httpRequest = null;
			HttpResponse httpResponse = null;
			HttpContext httpContext = GetContext(out httpRequest, out httpResponse);

			var timeoutInMinutes = (_TimeoutInSeconds / 60);
			var storeData = mongoSessionStateProvider.CreateNewStoreData(httpContext, timeoutInMinutes);
			storeData.Items["DummyEntry"] = "DummyValue";

			string sessionId = null;
			lock (_SessionIdManager)
			{
				sessionId = _SessionIdManager.CreateSessionID(httpContext);
			}

			object lockId = null; // New items don't have a lockId
			mongoSessionStateProvider.SetAndReleaseItemExclusive(httpContext, sessionId, storeData, lockId, true);

			TestContext.WriteLine("Created Session {0}", sessionId);

			int counter = _TimeoutInSeconds + 60;

			TestContext.WriteLine("Waiting {0} seconds...", counter);
			while (counter > 0)
			{
				System.Threading.Thread.Sleep(1000);
				counter--;
			}

			bool locked;
			TimeSpan lockAge;
			object lockId2 = null;
			System.Web.SessionState.SessionStateActions actions;
			var storeDataAfterExpiry = mongoSessionStateProvider.GetItem(httpContext, sessionId, out locked, out lockAge, out lockId2, out actions);

			if (storeDataAfterExpiry == null || storeDataAfterExpiry.Items.Count == 0)
			{
				TestContext.WriteLine("Session expired from Session State Provider.  Verifying session provider cache...");

				ISessionStateData objectInCache = null;
				bool objectExistsInCache = CheckSessionExistsInCache(mongoSessionStateProvider, sessionId, out objectInCache);

				if (objectInCache != null)
				{
					Assert.Fail("Session data exists in cache when should have expired - Expires = {0}", objectInCache.Expires);
				}
				else if (objectInCache == null && objectExistsInCache)
				{
					Assert.Fail("Session data has expired but the cache is retaining the object!");
				}
				else
				{
					TestContext.WriteLine("Success - session data does not exist in cache.");
				}
			}
			else
			{
				Assert.Fail("Session expired but MongoDB Sessions State Provider still contains data!");
			}
		}

		/// <summary>
		/// Creates sessions concurrently and stores a random binary blob in each session.  This
		/// method then waits for all the sessions to expire and verifies that each session has in
		/// fact been removed from the provider and the provider's memory cache.  It also takes a
		/// snapshot of the memory usage pre session creation, post session creation and post
		/// session expiration.  This method will fail is any session still exists in the provider
		/// or there was excessive memory usage.
		/// </summary>
		/// <param name="provider">The MongoDB provider to use for the test.</param>
		/// <param name="numberOfSessions">The number of sessions to create concurrently.</param>
		/// <param name="sessionSizeInMegabytes">The number of MB of buffer to create and store in each session.</param>
		private void CreateConcurrentSessionObjects(MongoDBSessionStateProvider provider, int numberOfSessions, byte sessionSizeInMegabytes)
		{
			var sessions = new System.Collections.Generic.SynchronizedCollection<string>();
			double initialWorkingSet, postSessionCreationWorkingSet, postSessionExpirationWorkingSet;

			var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
			initialWorkingSet = (currentProcess.WorkingSet64 / 1024.0 / 1000.0);

			int sessionSize = (sessionSizeInMegabytes * 1024 * 1024);
			var result = Parallel.For(0, numberOfSessions, idx =>
			{
				System.Threading.Thread.Sleep(50); // Give a little wiggle room

				HttpRequest request = null;
				HttpResponse response = null;
				HttpContext context = GetContext(out request, out response);

				string sessionId = _SessionIdManager.CreateSessionID(context);
				sessions.Add(sessionId);

				byte[] dummyData = new byte[sessionSize];
				(new Random()).NextBytes(dummyData);

				var dataStore = provider.CreateNewStoreData(context, (_TimeoutInSeconds / 60));
				dataStore.Items["Data"] = dummyData;
				provider.SetAndReleaseItemExclusive(context, sessionId, dataStore, null, true);

				TestContext.WriteLine("Created session {0} with dummy data", sessionId);
			});

			while (!result.IsCompleted)
			{
				System.Threading.Thread.Sleep(1000);
			}

			GC.Collect();
			currentProcess.Refresh();
			postSessionCreationWorkingSet = (currentProcess.WorkingSet64 / 1024.0 / 1000.0);

			var counter = _TimeoutInSeconds + 60;

			TestContext.WriteLine("Waiting {0} seconds for session expiration...", counter);

			while (counter > 0)
			{
				System.Threading.Thread.Sleep(1000);
				counter--;
			}

			TestContext.WriteLine("Checking Sessions in store and cache...");
			var sessionIds = sessions.ToArray();
			result = Parallel.ForEach<string>(sessionIds, sessionId =>
			{
				HttpRequest request = null;
				HttpResponse response = null;
				HttpContext context = GetContext(out request, out response);

				bool locked;
				TimeSpan lockAge;
				object lockId;
				System.Web.SessionState.SessionStateActions actions;
				var storeData = provider.GetItem(context, sessionId, out locked, out lockAge, out lockId, out actions);

				Assert.IsNull(storeData);

				// Now check the cache!
				ISessionStateData sessionStateData;
				bool existsInCache = CheckSessionExistsInCache(provider, sessionId, out sessionStateData);
				if (existsInCache || sessionStateData != null)
				{
					TestContext.WriteLine("Session {0} still exists in cache!", sessionId);
				}
				else
				{
					sessions.Remove(sessionId);
				}
			});

			while (!result.IsCompleted)
			{
				System.Threading.Thread.Sleep(1000);
			}

			GC.Collect();
			GC.WaitForPendingFinalizers();
			currentProcess.Refresh();
			postSessionExpirationWorkingSet = (currentProcess.WorkingSet64 / 1024.0 / 1000.0);

			TestContext.WriteLine("Memory Usage: Initial = {0}MB, PostSessionCreation = {1}MB, PostSessionExpiration = {2}MB", initialWorkingSet, postSessionCreationWorkingSet, postSessionExpirationWorkingSet);
			TestContext.WriteLine("After expiration, session count = {0}", sessions.Count);

			currentProcess.Dispose();

			double memoryDifference = postSessionExpirationWorkingSet - initialWorkingSet;
			TestContext.WriteLine("Memory Difference -> {0}MB", memoryDifference);

			bool isMemoryExhausted = (memoryDifference > 20); // This should be based on the buffer size and number of sessions
			if (sessions.Count != 0)
			{
				Assert.Fail("{0} Sessions still exist in memory.  The memory has grown from {1}MB to {2}MB", sessions.Count, initialWorkingSet, postSessionExpirationWorkingSet);
			}
			if (isMemoryExhausted)
			{
				Assert.Fail("Excessive Memory Consumption.  Memory has grown by {0}MB", memoryDifference);
			}
		}

		#endregion

		#region Helpers

		/// <summary>
		/// Creates a new instance of MongoDBSessionStateProvider
		/// </summary>
		/// <param name="useDotNetMemoryCache">Determines whether to use .NET's MemoryCache or CommonCore's Cache</param>
		/// <returns>A new MongoDB Session State Provider</returns>
		private MongoDBSessionStateProvider GetMongoDBSessionStateProvider(bool useDotNetMemoryCache)
		{
			string dotNetMemoryCacheName = null;

			try
			{
				var mongoWebSection = ConfigurationManager.GetSection("mongoDbWeb");
				var mongoSessionState = mongoWebSection.GetType().InvokeMember("SessionState", BindingFlags.GetProperty, null, mongoWebSection, null);
				dotNetMemoryCacheName = mongoSessionState.GetType().InvokeMember("DotNetMemoryCacheName", BindingFlags.GetProperty, null, mongoSessionState, null) as string;
			}
			catch { }

			if (useDotNetMemoryCache)
			{
				// Pull the name from the config, or else create one by default to ensure
				// mongoDbSessionStateProvider uses the .NET MemoryCache
				if (string.IsNullOrWhiteSpace(dotNetMemoryCacheName))
				{
					dotNetMemoryCacheName = "MongoDBMemoryCache";
				}
			}
			else
			{
				dotNetMemoryCacheName = string.Empty;
			}

			var config = new System.Collections.Specialized.NameValueCollection(_ProviderSettings.Parameters);
			config["dotNetMemoryCacheName"] = dotNetMemoryCacheName;

			var provider = new MongoDBSessionStateProvider();
			provider.Initialize(_ProviderSettings.Name, config);

			return provider;
		}

		/// <summary>
		/// Uses reflection to determine if the MongoDB Session State Provider is holding onto a sessionId
		/// in it's memory cache object.
		/// </summary>
		/// <param name="mongoSessionStateProvider">The provider to use</param>
		/// <param name="sessionId">The session id to query</param>
		/// <param name="dataFromCache">The data retrieved from the cache</param>
		/// <returns>True if the session exists in the provider's cache</returns>
		private bool CheckSessionExistsInCache(MongoDBSessionStateProvider mongoSessionStateProvider, string sessionId, out ISessionStateData dataFromCache)
		{
			var mongoSessionStateProviderBaseType = mongoSessionStateProvider.GetType().BaseType;

			bool objectExistsInCache = (bool)mongoSessionStateProviderBaseType
										.GetMethod("CacheContains", BindingFlags.NonPublic | BindingFlags.Instance)
										.Invoke(mongoSessionStateProvider, new object[] { sessionId });

			dataFromCache = (DefaultSessionStateData)mongoSessionStateProviderBaseType
									.GetMethod("CacheGet", BindingFlags.NonPublic | BindingFlags.Instance)
									.Invoke(mongoSessionStateProvider, new object[] { sessionId });

			return objectExistsInCache;
		}

		/// <summary>
		/// Creates a new pseudo HttpContext and uses reflection to mimic what would happen
		/// in an ASP.net hosting environment.  Reflection is used only on the properties required
		/// by the session state module.
		/// </summary>
		/// <param name="request">Pseudo request object used in the context</param>
		/// <param name="response">Pseudo response object used in the context</param>
		/// <returns>A pseudo HttpContext object</returns>
		private HttpContext GetContext(out HttpRequest request, out HttpResponse response)
		{
			request = new HttpRequest("dummy.txt", "http://localhost/dummy.txt", string.Empty);

			HttpApplication httpApp = new HttpApplication();
			HttpApplicationState stateValue = (HttpApplicationState)Activator.CreateInstance(typeof(HttpApplicationState), true);

			var httpAppFactoryType = httpApp.GetType().Assembly.GetType("System.Web.HttpApplicationFactory");
			var httpAppFactory = Activator.CreateInstance(httpAppFactoryType, true);
			var httpAppFactoryStaticInstanceField = httpAppFactoryType.GetField("_theApplicationFactory", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
			var theHttpAppFactory = httpAppFactoryStaticInstanceField.GetValue(httpAppFactory);

			var httpAppStateField = httpAppFactoryType.GetField("_state", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			httpAppStateField.SetValue(theHttpAppFactory, stateValue);

			var stateField = httpApp.GetType().GetField("_state", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			stateField.SetValue(httpApp, stateValue);

			response = new HttpResponse(null);

			return new HttpContext(request, response);
		}

		/// <summary>
		/// Uses reflection to update private members to mimic an ASP.net hosting environment
		/// to ensure that the MongoDB session state module is capable of running.
		/// </summary>
		private static void UpdateHostingEnvironment()
		{
			var virtualPathType = typeof(HttpContext).Assembly.GetType("System.Web.VirtualPath");
			var virtualPathObj = virtualPathType.InvokeMember("Create", BindingFlags.InvokeMethod, null, null, new object[] { "/" });

			var appVirtulPath = System.Web.Hosting.HostingEnvironment.ApplicationVirtualPath;

			System.Web.Hosting.HostingEnvironment env = new System.Web.Hosting.HostingEnvironment();
			var appVirtualPathField = env.GetType().GetField("_appVirtualPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			appVirtualPathField.SetValue(env, virtualPathObj);
		}

		#endregion
	}
}