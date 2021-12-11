using Kudu.Core.Functions;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;

namespace Kudu.Tests.Core.Function
{
    public class KedaFunctionTriggersProviderTests
    {
        [Fact]
        public void DurableFunctions_DeployAsZip()
        {
            // Generate a zip archive with a host.json and the contents of a Durable Function app
            string zipFilePath = Path.GetTempFileName();
            using (var fileStream = File.OpenWrite(zipFilePath))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                CreateJsonFileEntry(archive, "host.json", @"{""version"":""2.0"",""extensions"":{""durableTask"":{""hubName"":""DFTest"",""storageProvider"":{""type"":""mssql"",""connectionStringName"":""SQLDB_Connection""}}}}");
                CreateJsonFileEntry(archive, "f1/function.json", @"{""bindings"":[{""type"":""orchestrationTrigger"",""name"":""context""}],""disabled"":false}");
                CreateJsonFileEntry(archive, "f2/function.json", @"{""bindings"":[{""type"":""entityTrigger"",""name"":""ctx""}],""disabled"":false}");
                CreateJsonFileEntry(archive, "f3/function.json", @"{""bindings"":[{""type"":""activityTrigger"",""name"":""input""}],""disabled"":false}");
                CreateJsonFileEntry(archive, "f4/function.json", @"{""bindings"":[{""type"":""queueTrigger"",""connection"":""AzureWebjobsStorage"",""queueName"":""queue"",""name"":""queueItem""}],""disabled"":false}");
                CreateJsonFileEntry(archive, "f5/function.json", @"{""bindings"":[{""type"":""httpTrigger"",""methods"":[""post""],""authLevel"":""anonymous"",""name"":""req""}],""disabled"":false}");
            }

            try
            {
                IEnumerable<ScaleTrigger> result = KedaFunctionTriggerProvider.GetFunctionTriggers(zipFilePath);
                ValidateDurableTriggers(result);
            }
            finally
            {
                File.Delete(zipFilePath);
            }
        }

        internal static void ValidateDurableTriggers(IEnumerable<ScaleTrigger> triggers)
        {
            Assert.Equal(3, triggers.Count());

            ScaleTrigger mssqlTrigger = Assert.Single(triggers, trigger => trigger.Type.Equals("mssql", StringComparison.OrdinalIgnoreCase));
            string query = Assert.Contains("query", mssqlTrigger.Metadata);
            Assert.NotNull(query);
            Assert.Contains("dt.GetScaleRecommendation", query);

            string targetValue = Assert.Contains("targetValue", mssqlTrigger.Metadata);
            Assert.False(string.IsNullOrEmpty(targetValue));
            Assert.True(double.TryParse(targetValue, out _));

            string connectionStringName = Assert.Contains("connectionStringFromEnv", mssqlTrigger.Metadata);
            Assert.Equal("SQLDB_Connection", connectionStringName);

            ScaleTrigger queueTrigger = Assert.Single(triggers, trigger => trigger.Type.Equals("azure-queue", StringComparison.OrdinalIgnoreCase));
            string functionName = Assert.Contains("functionName", queueTrigger.Metadata);
            Assert.Equal("f4", functionName);

            ScaleTrigger httpTrigger = Assert.Single(triggers, trigger => trigger.Type.Equals("httpTrigger", StringComparison.OrdinalIgnoreCase));
            functionName = Assert.Contains("functionName", httpTrigger.Metadata);
            Assert.Equal("f5", functionName);
        }

        [Theory]
        [InlineData("flowc1712a574433c1djobtriggers00", "10", "haassyad-scaling-lima", "workflowApp", @"{""version"":""2.0"",""extensionBundle"":{""id"": ""Microsoft.Azure.Functions.ExtensionBundle.Workflows"", ""version"": ""[1.*, 2.0.0)""}, ""extensions"":{""workflow"":{""Settings"":{""Runtime.ScaleMonitor.KEDA.TargetQueueLength"":10}}}}")]
        [InlineData("flowdc234f1fbd9ff3fjobtriggers00", "20", "n/a", "workflowApp,functionapp", @"{""version"":""2.0"",""extensionBundle"":{""id"": ""Microsoft.Azure.Functions.ExtensionBundle.Workflows"", ""version"": ""[1.*, 2.0.0)""}, ""extensions"":{""workflow"":{""Settings"":{""Runtime.HostId"":""haassyad-applicationinsights""}}}}")]
        public void WorkflowApp( string expectedQueueName, string expectedQueueLength, string appName, string appType, string hostJsonText)
        {
            // Generate a zip archive with a host.json with workflow extension enabled
            string zipFilePath = Path.GetTempFileName();
            using (var fileStream = File.OpenWrite(zipFilePath))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                CreateJsonFileEntry(archive, "host.json", hostJsonText);
            }

            try
            {
                IEnumerable<ScaleTrigger> result = KedaFunctionTriggerProvider.GetFunctionTriggers(zipFilePath, appName, appType);
                Assert.Single(result);

                ScaleTrigger queueTrigger = Assert.Single(result, trigger => trigger.Type.Equals("azure-queue", StringComparison.OrdinalIgnoreCase));
                var actualQueueName = Assert.Contains("queueName", queueTrigger.Metadata);
                Assert.Equal(actualQueueName, expectedQueueName);

                var actualQueueLength = Assert.Contains("queueLength", queueTrigger.Metadata);
                Assert.Equal(actualQueueLength, expectedQueueLength);

                string connectionStringFromEnv = Assert.Contains("connectionFromEnv", queueTrigger.Metadata);
                Assert.Equal("AzureWebJobsStorage", connectionStringFromEnv);
            }
            finally
            {
                File.Delete(zipFilePath);
            }
        }

        private static void CreateJsonFileEntry(ZipArchive archive, string path, string content)
        {
            using (Stream entryStream = archive.CreateEntry(path).Open())
            using (var streamWriter = new StreamWriter(entryStream))
            {
                streamWriter.Write(content);
            }
        }

        [Fact]
        public void PopulateMetadataDictionary_CosmosDbTriggerV4()
        {
            // Test handling for the trigger defined in Cosmos DB WebJobs extension 4.0.0.
            var bindingJObject = new JObject
            {
                ["type"] = "cosmosDbTrigger",
                ["name"] = "myBinding",
                ["connection"] = "myConnection",
                ["databaseName"] = "myDatabaseName",
                ["containerName"] = "myContainerName",
                ["leaseConnection"] = "myLeaseConnection",
                ["leaseDatabaseName"] = "myLeaseDatabaseName",
                ["leaseContainerName"] = "myLeaseContainerName",
                ["leaseContainerPrefix"] = "myLeaseContainerPrefix",
            };

            IDictionary<string, string> metadata = KedaFunctionTriggerProvider.PopulateMetadataDictionary(bindingJObject, "myFunction");

            Assert.NotNull(metadata);
            Assert.NotEmpty(metadata);

            Assert.False(metadata.ContainsKey("type"));
            Assert.False(metadata.ContainsKey("name"));

            Assert.Equal("myConnection", metadata["connection"]);
            Assert.Equal("myDatabaseName", metadata["databaseId"]);
            Assert.Equal("myContainerName", metadata["containerId"]);
            Assert.Equal("myLeaseConnection", metadata["leaseConnection"]);
            Assert.Equal("myLeaseDatabaseName", metadata["leaseDatabaseId"]);
            Assert.Equal("myLeaseContainerName", metadata["leaseContainerId"]);
            Assert.Equal("myLeaseContainerPrefix", metadata["processorName"]);
        }

        [Fact]
        public void PopulateMetadataDictionary_CosmosDbTriggerV3()
        {
            // Test handling for the trigger defined in Cosmos DB WebJobs extension 3.10.0.
            var bindingJObject = new JObject
            {
                ["type"] = "cosmosDbTrigger",
                ["name"] = "myBinding",
                ["connectionStringSetting"] = "myConnectionStringSetting",
                ["databaseName"] = "myDatabaseName",
                ["collectionName"] = "myCollectionName",
                ["leaseConnectionStringSetting"] = "myLeaseConnectionStringSetting",
                ["leaseDatabaseName"] = "myLeaseDatabaseName",
                ["leaseCollectionName"] = "myLeaseCollectionName",
                ["leaseCollectionPrefix"] = "myLeaseCollectionPrefix",
            };

            IDictionary<string, string> metadata = KedaFunctionTriggerProvider.PopulateMetadataDictionary(bindingJObject, "myFunction");

            Assert.NotNull(metadata);
            Assert.NotEmpty(metadata);

            Assert.False(metadata.ContainsKey("type"));
            Assert.False(metadata.ContainsKey("name"));

            Assert.Equal("myConnectionStringSetting", metadata["connection"]);
            Assert.Equal("myDatabaseName", metadata["databaseId"]);
            Assert.Equal("myCollectionName", metadata["containerId"]);
            Assert.Equal("myLeaseConnectionStringSetting", metadata["leaseConnection"]);
            Assert.Equal("myLeaseDatabaseName", metadata["leaseDatabaseId"]);
            Assert.Equal("myLeaseCollectionName", metadata["leaseContainerId"]);
            Assert.Equal("myLeaseCollectionPrefix", metadata["processorName"]);
        }

        [Fact]
        public void PopulateMetadataDictionary_CosmosDbTriggerDefaults()
        {
            // Test default-value substitution if optional fields in trigger binding are not provided.
            var bindingJObject = new JObject
            {
                ["type"] = "cosmosDbTrigger",
                ["name"] = "myBinding",
                ["databaseName"] = "myDatabaseName",
                ["containerName"] = "myContainerName",
            };

            IDictionary<string, string> metadata = KedaFunctionTriggerProvider.PopulateMetadataDictionary(bindingJObject, "myFunction");

            Assert.NotNull(metadata);
            Assert.NotEmpty(metadata);

            Assert.False(metadata.ContainsKey("type"));
            Assert.False(metadata.ContainsKey("name"));

            Assert.Equal("CosmosDB", metadata["connection"]);
            Assert.Equal("myDatabaseName", metadata["databaseId"]);
            Assert.Equal("myContainerName", metadata["containerId"]);
            Assert.Equal("CosmosDB", metadata["leaseConnection"]);
            Assert.Equal("myDatabaseName", metadata["leaseDatabaseId"]);
            Assert.Equal("leases", metadata["leaseContainerId"]);
            Assert.Equal(string.Empty, metadata["processorName"]);
        }

         public void PopulateMetadataDictionary_KedaV1_CorrectlyPopulatesRabbitMQMetadata()
        {
            string jsonText = @"
            {
                ""type"": ""rabbitMQTrigger"",
                ""connectionStringSetting"": ""RabbitMQConnection"",
                ""queueName"": ""myQueue"",
                ""name"": ""message""
            }";

            JToken jsonObj = JToken.Parse(jsonText);

            IDictionary<string, string> metadata = KedaFunctionTriggerProvider.PopulateMetadataDictionary(jsonObj, "f1");

            Assert.Equal(4, metadata.Count);
            Assert.True(metadata.ContainsKey("type"));
            Assert.True(metadata.ContainsKey("host"));
            Assert.True(metadata.ContainsKey("name"));
            Assert.True(metadata.ContainsKey("queueName"));
            Assert.Equal("rabbitMQTrigger", metadata["type"]);
            Assert.Equal("RabbitMQConnection", metadata["host"]);
            Assert.Equal("message", metadata["name"]);
            Assert.Equal("myQueue", metadata["queueName"]);
        }

        [Fact]
        public void PopulateMetadataDictionary_KedaV2_CorrectlyPopulatesRabbitMQMetadata()
        {
            string jsonText = @"
            {
                ""type"": ""rabbitMQTrigger"",
                ""connectionStringSetting"": ""RabbitMQConnection"",
                ""queueName"": ""myQueue"",
                ""name"": ""message""
            }";

            JToken jsonObj = JToken.Parse(jsonText);

            IDictionary<string, string> metadata = KedaFunctionTriggerProvider.PopulateMetadataDictionary(jsonObj, "f1");

            Assert.Equal(3, metadata.Count);
            Assert.True(metadata.ContainsKey("queueName"));
            Assert.True(metadata.ContainsKey("hostFromEnv"));
            Assert.Equal("myQueue", metadata["queueName"]);
            Assert.Equal("RabbitMQConnection", metadata["hostFromEnv"]);
        }

        [Fact]
        public void PopulateMetadataDictionary_KedaV2_OnlyKedaSupportedTriggers()
        {
            string jsonText = @"
            {
                ""functionName"": ""f1"",
                ""bindings"": [{
                    ""type"": ""eventGridTrigger"",
                    ""methods"": [""GET""],
                    ""authLevel"": ""anonymous""
                }]
            }";
            
            var jsonObj = JObject.Parse(jsonText);

            var triggers = KedaFunctionTriggerProvider.GetFunctionTriggers(new[] { jsonObj }, string.Empty, new Dictionary<string, string>());

            Assert.Equal(0, triggers.Count());
        }

        [Fact]
        public void PopulateAuthenticationRef_For_KafkaTrigger()
        {
            IKedaAuthRefProvider authRefProvider = KedaFunctionTriggerProvider.getTriggerAuthProvider("kafkatrigger");
            Assert.IsType<KafkaTriggerKedaAuthProvider>(authRefProvider);
        }

        [Fact]
        public void ConversionFromSyncTriggerPayloadToFunctionTrigger()
        {
            var inputPayload = @"
{
    ""triggers"": [
        {
            ""name"": ""myQueueItem"",
            ""type"": ""queueTrigger"",
            ""direction"": ""in"",
            ""queueName"": ""js-queue-items"",
            ""connection"": ""AzureWebJobsStorage"",
            ""functionName"": ""QueueTrigger""
        }
    ],
    ""functions"": [
        {
            ""name"": ""QueueTrigger"",
            ""script_root_path_href"": ""https://tsushiququecon1901.limaarcncsenv19--k53aemd.northcentralusstage.k4apps.io/admin/vfs/home/site/wwwroot/QueueTrigger/"",
            ""script_href"": ""https://tsushiququecon1901.limaarcncsenv19--k53aemd.northcentralusstage.k4apps.io/admin/vfs/home/site/wwwroot/QueueTrigger/index.js"",
            ""config_href"": ""https://tsushiququecon1901.limaarcncsenv19--k53aemd.northcentralusstage.k4apps.io/admin/vfs/home/site/wwwroot/QueueTrigger/function.json"",
            ""test_data_href"": ""https://tsushiququecon1901.limaarcncsenv19--k53aemd.northcentralusstage.k4apps.io/admin/vfs/tmp/FunctionsData/QueueTrigger.dat"",
            ""href"": ""https://tsushiququecon1901.limaarcncsenv19--k53aemd.northcentralusstage.k4apps.io/admin/functions/QueueTrigger"",
            ""invoke_url_template"": null,
            ""language"": ""node"",
            ""config"": {
                ""bindings"": [
                    {
                        ""name"": ""myQueueItem"",
                        ""type"": ""queueTrigger"",
                        ""direction"": ""in"",
                        ""queueName"": ""js-queue-items"",
                        ""connection"": ""AzureWebJobsStorage""
                    }
                ]
            },
            ""files"": null,
            ""test_data"": """",
            ""isDisabled"": false,
            ""isDirect"": false,
            ""isProxy"": false
        }
    ]
}
";
            var expectedJObjectString = @"
        {
            ""name"": ""myQueueItem"",
            ""type"": ""queueTrigger"",
            ""direction"": ""in"",
            ""queueName"": ""js-queue-items"",
            ""connection"": ""AzureWebJobsStorage"",
            ""functionName"": ""QueueTrigger""
        }
";

            var expectedJObject = JObject.Parse(expectedJObjectString);
            IEnumerable<KedaFunctionTriggerProvider.FunctionTrigger> results = KedaFunctionTriggerProvider.ParseSyncTriggerPayload(inputPayload);
            var actual = results.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal("QueueTrigger", actual.FunctionName);
            Assert.Equal("myQueueItem", actual.Binding["name"]);
            Assert.Equal("js-queue-items", actual.Binding["queueName"]);
            Assert.Equal("in", actual.Binding["direction"]);
            Assert.Equal("AzureWebJobsStorage", actual.Binding["connection"]);
        }

        [Fact]
        public void UpdateFunctionTriggerBindingExpression_Replace_Expression()
        {
            IEnumerable<ScaleTrigger> triggers = new ScaleTrigger[]
            {
                new ScaleTrigger
                {
                    Type = "kafkaTrigger",
                    Metadata = new Dictionary<string, string>()
                    {
                        {"brokerList", "BrokerList" },
                        {"topic", "%topic%" },
                        {"ConsumerGroup", "%ConsumerGroup%" }
                    }
                }
            };


            var appSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"topic", "myTopic"},
                {"consumergroup", "$Default"} // should be case insensitive
            };

            KedaFunctionTriggerProvider.UpdateFunctionTriggerBindingExpression(triggers, appSettings);
            var metadata = triggers?.FirstOrDefault().Metadata;
            Assert.Equal("myTopic", metadata["topic"]);
            Assert.Equal("$Default", metadata["ConsumerGroup"] );
            Assert.Equal("BrokerList", metadata["brokerList"]);
        }
    }
}
