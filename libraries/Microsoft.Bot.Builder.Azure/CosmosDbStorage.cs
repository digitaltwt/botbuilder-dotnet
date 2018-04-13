﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents.Linq;
using Microsoft.Bot.Builder.Core.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Microsoft.Bot.Builder.Azure
{
    /// <summary>
    /// Models IStorage around CosmosDB
    /// </summary>
    public class CosmosDbStorage : IStorage
    {
        private readonly string _databaseId;
        private readonly string _collectionId;
        private readonly Lazy<string> _collectionReady;
        private readonly DocumentClient _client;

        private static JsonSerializer _jsonSerializer = JsonSerializer.Create(new JsonSerializerSettings()
        {
            TypeNameHandling = TypeNameHandling.All
        });

        /// <summary>
        /// Initializes a new instance of <see cref="CosmosDbStorage"/> class,
        /// using the provided CosmosDB credentials, DatabaseId and CollectionId.
        /// </summary>
        /// <remarks>
        /// The ConnectionPolicy delegate can be used to further customize the connection to CosmosDB (Connection mode, retry options, timeouts).
        /// More information at https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.documents.client.connectionpolicy?view=azure-dotnet
        /// </remarks>
        /// <param name="serviceEndpoint">The endpoint Uri for the service endpoint from the Azure Cosmos DB service.</param>
        /// <param name="authKey">The AuthKey used by the client from the Azure Cosmos DB service.</param>
        /// <param name="databaseId">The Database ID.</param>
        /// <param name="collectionId">The Collection ID.</param>
        /// <param name="connectionPolicyConfigurator">
        ///     A Delegate to further customize the ConnectionPolicy to CosmosDB.
        ///     More information at https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.documents.client.connectionpolicy?view=azure-dotnet
        /// </param>
        public CosmosDbStorage(Uri serviceEndpoint, string authKey, string databaseId, string collectionId, Action<ConnectionPolicy> connectionPolicyConfigurator = null)
        {
            if (serviceEndpoint == null)
            {
                throw new ArgumentNullException(nameof(serviceEndpoint), "Service EndPoint for CosmosDB is required.");
            }

            if (string.IsNullOrEmpty(authKey))
            {
                throw new ArgumentException("AuthKey for CosmosDB is required.", nameof(authKey));
            }

            if (string.IsNullOrEmpty(databaseId))
            {
                throw new ArgumentException("DatabaseId is required.", nameof(databaseId));
            }

            if (string.IsNullOrEmpty(collectionId))
            {
                throw new ArgumentException("CollectionId is required.", nameof(collectionId));
            }

            _databaseId = databaseId;
            _collectionId = collectionId;

            // Inject BotBuilder version to CosmosDB Requests
            var version = GetType().Assembly.GetName().Version;
            var connectionPolicy = new ConnectionPolicy()
            {
                UserAgentSuffix = $"Microsoft-BotFramework {version}"
            };

            // Invoke CollectionPolicy delegate to further customize settings
            connectionPolicyConfigurator?.Invoke(connectionPolicy);
            _client = new DocumentClient(serviceEndpoint, authKey, connectionPolicy);

            // Delayed Database and Collection creation if they do not exist. This is a thread-safe operation.
            _collectionReady = new Lazy<string>(() =>
            {
                _client.CreateDatabaseIfNotExistsAsync(new Database { Id = _databaseId })
                    .Wait();

                var task = _client.CreateDocumentCollectionIfNotExistsAsync(UriFactory.CreateDatabaseUri(_databaseId), new DocumentCollection { Id = _collectionId });
                task.Wait();

                return task.Result.Resource.SelfLink;
            });
        }

        /// <summary>
        /// Deletes the specified keys from storage.
        /// </summary>
        /// <param name="keys">List of keys to remove.</param>
        /// <returns></returns>
        public async Task Delete(params string[] keys)
        {
            if (keys.Length == 0) return;

            // Ensure collection exists
            var collectionLink = _collectionReady.Value;

            // Parallelize deletion
            var tasks = keys.Select(key =>
            {
                return _client.DeleteDocumentAsync(UriFactory.CreateDocumentUri(_databaseId, _collectionId, SanitizeKey(key)));
            });

            // await to deletion tasks to complete
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns an instance of <see cref="StoreItems"/> bag with the values for the specified  keys.
        /// </summary>
        /// <param name="keys">List of keys to retrieve.</param>
        /// <returns>A <see cref="StoreItems"/> instance.</returns>
        public async Task<StoreItems> Read(params string[] keys)
        {
            if (keys.Length == 0)
            {
                throw new ArgumentException("Please provide at least one key to read from storage", nameof(keys));
            }

            // Ensure collection exists
            var collectionLink = _collectionReady.Value;
            var storeItems = new StoreItems();

            var parameterSequence = string.Join(",", Enumerable.Range(0, keys.Length).Select(i => $"@id{i}"));
            var parameterValues = keys.Select((key, ix) => new SqlParameter($"@id{ix}", SanitizeKey(key)));
            var querySpec = new SqlQuerySpec
            {
                QueryText = $"SELECT c.id, c.realId, c.document, c._etag FROM c WHERE c.id in ({parameterSequence})",
                Parameters = new SqlParameterCollection(parameterValues)
            };

            var query = _client.CreateDocumentQuery<DocumentStoreItem>(collectionLink, querySpec).AsDocumentQuery();
            while (query.HasMoreResults)
            {
                foreach (var doc in await query.ExecuteNextAsync<DocumentStoreItem>())
                {
                    var item = doc.Document.ToObject(typeof(object), _jsonSerializer);
                    if (item is IStoreItem storeItem)
                    {
                        storeItem.eTag = doc.ETag;
                    }

                    // doc.Id cannot be used since it is escaped, read it from RealId property instead
                    storeItems[doc.ReadlId] = item;
                }
            }

            return storeItems;
        }

        /// <summary>
        /// Persist the specified StoreItems into the CosmosDB Collection.
        /// </summary>
        /// <param name="changes">Items to persist.</param>
        /// <returns></returns>
        public async Task Write(StoreItems changes)
        {
            if (changes == null)
            {
                throw new ArgumentNullException(nameof(changes), "Please provide a StoreItems with changes to persist.");
            }

            var collectionLink = _collectionReady.Value;
            foreach (var change in changes)
            {
                var json = JObject.FromObject(change.Value, _jsonSerializer);

                // Remove etag from JSON object that was copied from IStoreItem.
                // The ETag information is updated as an _etag attribute in the document metadata.
                json.Remove("eTag");

                var documentChange = new DocumentStoreItem
                {
                    Id = SanitizeKey(change.Key),
                    ReadlId = change.Key,
                    Document = json
                };

                string eTag = (change.Value as IStoreItem)?.eTag;
                if (eTag == null || eTag == "*")
                {
                    // if new item or * then insert or replace unconditionaly
                    await _client.UpsertDocumentAsync(collectionLink, documentChange, disableAutomaticIdGeneration: true).ConfigureAwait(false);
                }
                else if (eTag.Length > 0)
                {
                    // if we have an etag, do opt. concurrency replace
                    var uri = UriFactory.CreateDocumentUri(_databaseId, _collectionId, documentChange.Id);
                    var ac = new AccessCondition { Condition = eTag, Type = AccessConditionType.IfMatch };
                    await _client.ReplaceDocumentAsync(uri, documentChange, new RequestOptions { AccessCondition = ac }).ConfigureAwait(false);
                }
                else
                {
                    throw new Exception("etag empty");
                }
            }
        }

        /// <summary>
        /// Converts the key into a DocumentID that can be used safely with CosmosDB.
        /// The following characters are restricted and cannot be used in the Id property: '/', '\', '?', '#'
        /// More information at https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.documents.resource.id?view=azure-dotnet#remarks
        /// </summary>
        private static string SanitizeKey(string key)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in key)
            {
                if (_badChars.Value.TryGetValue(ch, out string val))
                    sb.Append(val);
                else
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        private static Lazy<Dictionary<char, string>> _badChars = new Lazy<Dictionary<char, string>>(() =>
        {
            char[] badChars = new char[] { '\\', '?', '/', '#', ' ' };
            var dict = new Dictionary<char, string>();
            foreach (var badChar in badChars)
                dict[badChar] = '*' + ((int)badChar).ToString("x2");
            return dict;
        });

        /// <summary>
        /// Internal data structure for storing items in a CosmosDB Collection.
        /// </summary>
        private class DocumentStoreItem
        {
            /// <summary>
            /// Sanitized Id/Key an used as PrimaryKey.
            /// </summary>
            [JsonProperty("id")]
            public string Id { get; set; }

            /// <summary>
            /// Un-sanitized Id/Key.
            /// </summary>
            [JsonProperty("realId")]
            public string ReadlId { get; internal set; }

            /// <summary>
            /// The persisted object.
            /// </summary>
            [JsonProperty("document")]
            public JObject Document { get; set; }

            /// <summary>
            /// ETag information for handling optimistic concurrency updates.
            /// </summary>
            [JsonProperty("_etag")]
            public string ETag { get; set; }
        }
    }
}
