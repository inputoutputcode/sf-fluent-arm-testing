// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Threading;
using System.Text;

using Xunit;
using Xunit.Abstractions;
using Newtonsoft.Json;

using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.Azure.Test.HttpRecorder;
using Microsoft.Rest.ClientRuntime.Azure.TestFramework;

using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.Compute.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.Storage.Fluent;
using Microsoft.Azure.Management.ServiceFabric.Fluent.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.Network.Fluent;
using Microsoft.Azure.Management.Network.Fluent.Models;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.Management.KeyVault.Fluent;
using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Azure.Tests;
using Fluent.Tests.Common;

using Assert = Xunit.Assert;
using Environment = System.Environment;


namespace Fluent.Tests
{
    public class ServiceFabric
    {
        private readonly ITestOutputHelper output;

        public ServiceFabric(ITestOutputHelper output)
        {
            this.output = output;
        }

        internal class AzureCliToken
        {
            [JsonProperty(PropertyName = "_authority")]
            public string Authority { get; set; }

            [JsonProperty(PropertyName = "_clientId")]
            public string Id { get; set; }

            [JsonProperty(PropertyName = "tokenType")]
            public string TokenType { get; set; }

            [JsonProperty(PropertyName = "expiresIn")]
            public long ExpiresIn { get; set; }

            [JsonProperty(PropertyName = "expiresOn")]
            public string ExpiresOn { get; set; }

            [JsonProperty(PropertyName = "oid")]
            public string Oid { get; set; }

            [JsonProperty(PropertyName = "userId")]
            public string UserId { get; set; }

            [JsonProperty(PropertyName = "servicePrincipalId")]
            public string ServicePrincipalId { get; set; }

            [JsonProperty(PropertyName = "servicePrincipalTenant")]
            public string ServicePrincipalTenant { get; set; }

            [JsonProperty(PropertyName = "isMRRT")]
            public bool IsMRRT { get; set; }

            [JsonProperty(PropertyName = "resource")]
            public string Resource { get; set; }

            [JsonProperty(PropertyName = "accessToken")]
            public string AccessToken { get; set; }

            [JsonProperty(PropertyName = "refreshToken")]
            public string RefreshToken { get; set; }

            [JsonProperty(PropertyName = "identityProvider")]
            public string IdentityProvider { get; set; }

            public bool IsServicePrincipal()
            {
                return ServicePrincipalId != null;
            }

            public string Tenant()
            {
                if (IsServicePrincipal())
                {
                    return ServicePrincipalTenant;
                }
                else
                {
                    string[] parts = Authority.Split('/');
                    return parts[parts.Length - 1];
                }
            }

            public string User()
            {
                if (IsServicePrincipal())
                {
                    return ServicePrincipalId;
                }
                else
                {
                    return UserId;
                }
            }

            public string ClientId()
            {
                if (IsServicePrincipal())
                {
                    return ServicePrincipalId;
                }
                else
                {
                    return Id;
                }
            }
        }

        internal class UserInfo
        {
            [JsonProperty(PropertyName = "type")]
            public string Type { get; set; }
            [JsonProperty(PropertyName = "name")]
            public string Name { get; set; }
        }

        internal class AzureCliSubscription
        {
            [JsonProperty(PropertyName = "environmentName")]
            public string EnvironmentName { get; set; }

            [JsonProperty(PropertyName = "id")]
            public string Id { get; set; }

            [JsonProperty(PropertyName = "name")]
            public string Name { get; set; }

            [JsonProperty(PropertyName = "tenantId")]
            public string TenantId { get; set; }

            [JsonProperty(PropertyName = "state")]
            public string State { get; set; }

            [JsonProperty(PropertyName = "user")]
            public UserInfo User { get; set; }

            [JsonProperty(PropertyName = "clientId")]
            public string ClientId { get; set; }

            [JsonProperty(PropertyName = "isDefault")]
            public bool IsDefault { get; set; }


            private AzureCliToken azureCliToken;

            public bool IsServicePrincipal()
            {
                return string.Equals(User.Type, "ServicePrincipal", StringComparison.OrdinalIgnoreCase);
            }

            public string UserName()
            {
                return User.Name;
            }

            public AzureCliSubscription WithToken(AzureCliToken token)
            {
                if (ClientId == null)
                {
                    ClientId = token.ClientId();
                }
                azureCliToken = token;
                return this;
            }

            public AzureCliToken Token()
            {
                return azureCliToken;
            }

            public AzureEnvironment Environment()
            {
                if (EnvironmentName == null)
                {
                    return null;
                }
                else if (string.Equals(EnvironmentName, "AzureCloud", StringComparison.OrdinalIgnoreCase))
                {
                    return AzureEnvironment.AzureGlobalCloud;
                }
                else if (string.Equals(EnvironmentName, "AzureChinaCloud", StringComparison.OrdinalIgnoreCase))
                {
                    return AzureEnvironment.AzureChinaCloud;
                }
                else if (string.Equals(EnvironmentName, "AzureGermanCloud", StringComparison.OrdinalIgnoreCase))
                {
                    return AzureEnvironment.AzureGermanCloud;
                }
                else if (string.Equals(EnvironmentName, "AzureUSGovernment", StringComparison.OrdinalIgnoreCase))
                {
                    return AzureEnvironment.AzureUSGovernment;
                }
                else
                {
                    return null;
                }
            }
        }


        internal class AzureCliSubscriptionWrapper
        {
            [JsonProperty(PropertyName = "subscriptions")]
            public IEnumerable<AzureCliSubscription> Subscriptions { get; set; }
        }

        private void TestAzureCliLogin()
        {
            string userProfile = "%USERPROFILE%";
            string home = "HOME";
            string linuxHome = "AZURE_CONFIG_DIR";
            string azureCliFolder = ".azure";
            string azureProfileFile = "azureProfile.json";
            string accessTokensFile = "accessTokens.json";
            AzureCliSubscription defaultSubscription = null;

            string homeDir = Environment.ExpandEnvironmentVariables(userProfile);
#if NETSTANDARD1_4
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                homeDir = Environment.GetEnvironmentVariable(home);
            }
#else
            if (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX)
            {
                homeDir = Environment.GetEnvironmentVariable(linuxHome);
            }
#endif

            output.WriteLine($"homeDir: {homeDir}");
            output.WriteLine($"accessTokenPath> {Path.Combine(Path.GetTempPath(), azureCliFolder, accessTokensFile)}");

            string azureProfilePath = Path.Combine(homeDir, azureCliFolder, azureProfileFile);
            string accessTokensPath = Path.Combine(Path.GetTempPath(), azureCliFolder, accessTokensFile);

            string azureProfileText = File.ReadAllText(azureProfilePath);
            string accessTokensText = string.Empty;

            try
            {
                accessTokensText = File.ReadAllText(accessTokensPath);
            }
            catch (Exception ex)
            {
                output.WriteLine($"ex: {ex.Message}");

                if (ex.InnerException != null)
                    output.WriteLine($"ex.InnerException: {ex.InnerException.Message}");
            }
            
            AzureCliSubscriptionWrapper wrapper = JsonConvert.DeserializeObject<AzureCliSubscriptionWrapper>(azureProfileText);
            IEnumerable<AzureCliToken> tokens = JsonConvert.DeserializeObject<IEnumerable<AzureCliToken>>(accessTokensText);

            output.WriteLine($"azureProfileText: {azureProfileText}");
            output.WriteLine($"accessTokensText: {accessTokensText}");


            while (true)
            {
                wrapper = JsonConvert.DeserializeObject<AzureCliSubscriptionWrapper>(File.ReadAllText(azureProfilePath));
                tokens = JsonConvert.DeserializeObject<IEnumerable<AzureCliToken>>(File.ReadAllText(accessTokensPath));

                if (wrapper == null || tokens == null || !tokens.Any() || wrapper.Subscriptions == null || !wrapper.Subscriptions.Any())
                {
                    output.WriteLine("Please login in Azure CLI and press any key to continue after you've successfully logged in.");
                }
                else
                {
                    break;
                }
            }

            foreach (AzureCliSubscription subscriptionItem in wrapper.Subscriptions)
            {
                foreach (AzureCliToken token in tokens)
                {
                    if (subscriptionItem.IsServicePrincipal() == token.IsServicePrincipal()
                        && string.Equals(subscriptionItem.UserName(), token.User(), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(subscriptionItem.TenantId, token.Tenant(), StringComparison.OrdinalIgnoreCase))
                    {
                        if (subscriptionItem.IsDefault)
                        {
                            defaultSubscription = subscriptionItem.WithToken(token);
                        }
                    }
                }
            }

            if (defaultSubscription == null)
            {
                throw new Exception("Please login in Azure CLI with service principal.");
            }

            //AzureCliSubscription subscription = azureCliCredentials.Subscription();
            //SdkContext.AzureCredentialsFactory.FromServicePrincipal(subscription.ClientId, subscription.Token().AccessToken, subscription.TenantId, subscription.Environment());
        }

        [Fact]
        public void CanCreateBasicCluster()
        {
            TestAzureCliLogin();

            using (var mockContext = FluentMockContext.Start(this.GetType().FullName))
            {
                #region Parameters / Setup

                var region = Region.USEast;
                string deploymentName = "fb" + DateTime.Now.ToString("ddHHmm");
                string resourceGroupName = deploymentName;
                string vaultName = deploymentName + "kv";
                string storageAccountName = deploymentName + "dg";
                string storageAccountName2 = deploymentName + "sf";
                string vnetName = deploymentName + "vnet";
                string publicIpName = deploymentName + "ip";
                string loadBalancerName1 = deploymentName + "lb1";
                string frontendName = loadBalancerName1 + "fe1";
                string backendPoolName1 = loadBalancerName1 + "bap1";
                string httpProbe = "httpProbe";
                string fabricGatewayProbe = "fabricGatewayProbe";
                string fabricHttpGatewayProbe = "fabricHttpGatewayProbe";
                string httpLoadBalancingRule = "httpRule";
                string fabricGatewayLoadBalancingRule = "fabricGatewayLoadBalancingRule";
                string fabricHttpGatewayLoadBalancingRule = "fabricHttpGatewayLoadBalancingRule";
                string vmssName = deploymentName + "vmss";
                string rdpNatPool = "rdpNatPool";
                string userName = "FabricMonkey";
                string password = "StrongPass!12"; 

                string clusterName = deploymentName + "sf";
                string clusterCertificateName = deploymentName + "clustercert";
                string clientCertificateName = deploymentName + "clusterclientcert";
                string proxyCertificateName = deploymentName + "proxycert";
                string clusterDnsName = clusterName + "." + region.Name + ".cloudapp.azure.com";
                string nodeTypeName = "frontend";
                string subnetName = "frontend";

                X509Certificate2 clusterCertificate = null;




                var resourceManager = TestHelper.CreateResourceManager();
                var keyVaultManager = TestHelper.CreateKeyVaultManager();
                var storageManager = TestHelper.CreateStorageManager();
                var networkManager = TestHelper.CreateNetworkManager();
                var computeManager = TestHelper.CreateComputeManager();
                var serviceFabricManager = TestHelper.CreateServiceFabricManager();

                #endregion

                try
                {
                    var resourceGroup = CreateResourceGroup(region, resourceGroupName, resourceManager);
                    var vault1 = CreateKeyVault(region, vaultName, keyVaultManager, resourceGroup);
                    var secretBundle = CreateCertificate(clusterDnsName, keyVaultManager, vault1);

                    var secretBytes = Convert.FromBase64String(secretBundle.Value);
                    var certCollection = new X509Certificate2Collection();
                    certCollection.Import(secretBytes, null, X509KeyStorageFlags.Exportable);
                    byte[] protectedCertificateBytes = certCollection.Export(X509ContentType.Pkcs12, password);
                    clusterCertificate = new X509Certificate2(protectedCertificateBytes, password);

                    // Install the certificate for SFX/ClusterConnection locally
                    var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(clusterCertificate);

                    var storageAccountDiagnostics = CreateStorageAccount(storageAccountName, region, storageManager, resourceGroup);
                    var storageVmDisks = CreateStorageAccount(storageAccountName2, region, storageManager, resourceGroup);
                    var nsg = CreateNSGs(region, resourceGroupName, networkManager, resourceGroup);
                    var network = CreateNetwork(region, vnetName, subnetName, networkManager, resourceGroup, nsg);
                    var publicIPAddress = CreatePip(region, publicIpName, networkManager, resourceGroup);
                    var loadBalancer1 = CreateLoadBalancer(region, loadBalancerName1, frontendName, backendPoolName1, httpProbe, fabricGatewayProbe, fabricHttpGatewayProbe, httpLoadBalancingRule, fabricGatewayLoadBalancingRule, fabricHttpGatewayLoadBalancingRule, rdpNatPool, networkManager, resourceGroup, publicIPAddress);

                    var serviceFabricCluster = serviceFabricManager.ServiceFabricClusters.Define(clusterName)
                        .WithRegion(region)
                        .WithExistingResourceGroup(resourceGroup)
                        .WithWindowsImage()
                        .WithReliabilityLevel(ReliabilityLevel.Silver)
                        .WithOneCertificateOnly(clusterCertificate)
                        .WithStorageAccountDiagnostics(storageAccountDiagnostics)
                        .AddNodeType(nodeTypeName)
                        .WithDefaults()
                        .Create();
                    
                    var scaleSet = CreateScaleSet(region, backendPoolName1, vmssName, rdpNatPool, userName, password, subnetName, computeManager, resourceGroup, storageAccountDiagnostics, network, loadBalancer1, clusterCertificate.Thumbprint, vault1, secretBundle.SecretIdentifier.Identifier, nodeTypeName, serviceFabricCluster.ClusterEndpoint);

                    int totalWaitTimeInSeconds = 0;
                    int waitTimeInSeconds = 15;
                    while (serviceFabricCluster.ClusterState != ClusterState.Ready)
                    {
                        serviceFabricCluster = serviceFabricCluster.Refresh();
                        SdkContext.DelayProvider.Delay(waitTimeInSeconds * 1000);

                        if ((totalWaitTimeInSeconds += waitTimeInSeconds) > 216000) // 60 mins
                        {
                            throw new Exception("Provisioning failed.");
                        }
                    }

                    Func<CancellationToken, Task<SecuritySettings>> GetSecurityCredentials = (ct) =>
                    {
                        // get the X509Certificate2 either from Certificate store or from file.
                        var remoteSecuritySettings = new RemoteX509SecuritySettings(new List<string> { clusterCertificate.Thumbprint });
                        return Task.FromResult<SecuritySettings>(new X509SecuritySettings(clusterCertificate, remoteSecuritySettings));
                    };

                    var serviceFabricClient = new ServiceFabricClientBuilder()
                        .UseEndpoints(new Uri($"https://{clusterDnsName}:19080"))
                        .UseX509Security(GetSecurityCredentials)
                        .BuildAsync().GetAwaiter().GetResult();


                    var clusterHealth = serviceFabricClient.Cluster.GetClusterHealthAsync().Result;

                    Assert.True(clusterHealth.UnhealthyEvaluations.Count() == 0);
                }
                finally
                {
                    try
                    {
                        //TestHelper.CreateResourceManager().ResourceGroups.BeginDeleteByName(resourceGroupName);

                        //// Remove the certificate
                        //var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                        //store.Open(OpenFlags.ReadWrite);
                        //store.Remove(clusterCertificate);
                    }
                    catch { }
                }
            }
        }

        private string GeneratePassword(int length)
        {
            var password = new StringBuilder();
            Random random = new Random();
            bool digit = false;
            bool lowercase = false;
            bool uppercase = false;
            bool nonAlphanumeric = false;

            while (password.Length < length)
            {
                char c = (char)random.Next(32, 126);

                password.Append(c);

                if (char.IsDigit(c))
                    digit = false;
                else if (char.IsLower(c))
                    lowercase = false;
                else if (char.IsUpper(c))
                    uppercase = false;
                else if (!char.IsLetterOrDigit(c))
                    nonAlphanumeric = false;
            }

            if (nonAlphanumeric)
                password.Append((char)random.Next(33, 48));
            if (digit)
                password.Append((char)random.Next(48, 58));
            if (lowercase)
                password.Append((char)random.Next(97, 123));
            if (uppercase)
                password.Append((char)random.Next(65, 91));

            return password.ToString();
        }

        private static INetworkSecurityGroup CreateNSGs(Region region, string resourceGroupName, INetworkManager manager, IResourceGroup resourceGroup)
        {
            var frontEndNSG = manager.NetworkSecurityGroups.Define(resourceGroupName + "nsg")
                                .WithRegion(region)
                                .WithExistingResourceGroup(resourceGroup)
                                .DefineRule("Gateway")
                                    .AllowInbound()
                                    .FromAddress("Internet")
                                    .FromAnyPort()
                                    .ToAddress("VirtualNetwork")
                                    .ToPort(19000)
                                    .WithProtocol(SecurityRuleProtocol.Tcp)
                                    .WithPriority(3900)
                                    .WithDescription("Allow Service Fabric Gateway.")
                                    .Attach()
                                .DefineRule("HttpGateway")
                                    .AllowInbound()
                                    .FromAddress("Internet")
                                    .FromAnyPort()
                                    .ToAddress("VirtualNetwork")
                                    .ToPort(19080)
                                    .WithProtocol(SecurityRuleProtocol.Tcp)
                                    .WithPriority(3910)
                                    .WithDescription("Allow Service Fabric HTTP Gateway.")
                                    .Attach()
                                .DefineRule("Lease")
                                    .AllowInbound()
                                    .FromAddress("VirtualNetwork")
                                    .FromAnyPort()
                                    .ToAddress("VirtualNetwork")
                                    .ToPortRange(1025, 1027)
                                    .WithProtocol(SecurityRuleProtocol.Tcp)
                                    .WithPriority(3920)
                                    .WithDescription("Allow lease layer for Service Fabric.")
                                    .Attach()
                                .DefineRule("Ephemeral")
                                    .AllowInbound()
                                    .FromAddress("VirtualNetwork")
                                    .FromAnyPort()
                                    .ToAddress("VirtualNetwork")
                                    .ToPortRange(49152, 65534)
                                    .WithProtocol(SecurityRuleProtocol.Tcp)
                                    .WithPriority(3930)
                                    .WithDescription("Allow ephemeral ports for Service Fabric.")
                                    .Attach()
                                .DefineRule("Application")
                                    .AllowInbound()
                                    .FromAddress("VirtualNetwork")
                                    .FromAnyPort()
                                    .ToAddress("VirtualNetwork")
                                    .ToPortRange(20000, 30000)
                                    .WithProtocol(SecurityRuleProtocol.Tcp)
                                    .WithPriority(3940)
                                    .WithDescription("Allow application ports between nodes.")
                                    .Attach()
                                .DefineRule("SMB")
                                    .AllowInbound()
                                    .FromAddress("VirtualNetwork")
                                    .FromAnyPort()
                                    .ToAddress("VirtualNetwork")
                                    .ToPort(445)
                                    .WithProtocol(SecurityRuleProtocol.Tcp)
                                    .WithPriority(3950)
                                    .WithDescription("Allow SMB for ImageStore service between nodes.")
                                    .Attach()
                                .DefineRule("RDP")
                                    .AllowInbound()
                                    .FromAddress("Internet")
                                    .FromAnyPort()
                                    .ToAddress("VirtualNetwork")
                                    .ToPortRange(3389, 3488)
                                    .WithProtocol(SecurityRuleProtocol.Tcp)
                                    .WithPriority(3960)
                                    .WithDescription("Allow RDP to nodes.")
                                    .Attach()
                                .DefineRule("HttpEndpoint")
                                    .AllowInbound()
                                    .FromAddress("Internet")
                                    .FromAnyPort()
                                    .ToAddress("VirtualNetwork")
                                    .ToPort(80)
                                    .WithProtocol(SecurityRuleProtocol.Tcp)
                                    .WithPriority(3980)
                                    .WithDescription("Allow HTTP traffic for custom endpoint.")
                                    .Attach()
                                .DefineRule("Network")
                                    .AllowOutbound()
                                    .FromAddress("VirtualNetwork")
                                    .FromAnyPort()
                                    .ToAddress("VirtualNetwork")
                                    .ToAnyPort()
                                    .WithProtocol(SecurityRuleProtocol.Tcp)
                                    .WithPriority(3900)
                                    .WithDescription("Allow SMB for ImageStore service between nodes.")
                                    .Attach()
                                .DefineRule("ResourceProvider")
                                    .AllowOutbound()
                                    .FromAddress("VirtualNetwork")
                                    .FromAnyPort()
                                    .ToAddress("Internet")
                                    .ToPort(443)
                                    .WithProtocol(SecurityRuleProtocol.Tcp)
                                    .WithPriority(3910)
                                    .WithDescription("Allow to connect to the Service Fabric Resource Provider.")
                                    .Attach()
                                .DefineRule("Upgrade")
                                    .AllowOutbound()
                                    .FromAddress("VirtualNetwork")
                                    .FromAnyPort()
                                    .ToAddress("Internet")
                                    .ToPort(443)
                                    .WithProtocol(SecurityRuleProtocol.Tcp)
                                    .WithPriority(3920)
                                    .WithDescription("Allow Service Fabric to download new runtime versions.")
                                    .Attach()
                                .Create();

            return frontEndNSG;
        }

        private static INetwork CreateNetwork(Region region, string vnetName, string subnetName, INetworkManager manager, IResourceGroup resourceGroup, INetworkSecurityGroup nsg)
        {
            return manager.Networks.Define(vnetName)
                                        .WithRegion(region)
                                        .WithExistingResourceGroup(resourceGroup)
                                        .WithAddressSpace("10.0.0.0/16")
                                        .DefineSubnet(subnetName)
                                        .WithAddressPrefix("10.0.0.0/16")
                                        .WithExistingNetworkSecurityGroup(nsg)
                                        .Attach()
                                        .Create();
        }

        private static IPublicIPAddress CreatePip(Region region, string publicIpName, INetworkManager networkManager, IResourceGroup resourceGroup)
        {
            return networkManager.PublicIPAddresses.Define(publicIpName)
                    .WithRegion(region)
                    .WithExistingResourceGroup(resourceGroup)
                    .WithLeafDomainLabel(publicIpName)
                    .WithSku(PublicIPSkuType.Standard)
                    .WithStaticIP()
                    .Create();
        }

        private static ILoadBalancer CreateLoadBalancer(Region region, string loadBalancerName1, string frontendName, string backendPoolName1, string httpProbe, string fabricGatewayProbe, string fabricHttpGatewayProbe, string httpLoadBalancingRule, string fabricGatewayLoadBalancingRule, string fabricHttpGatewayLoadBalancingRule, string rdpNatPool, INetworkManager networkManager, IResourceGroup resourceGroup, IPublicIPAddress publicIPAddress)
        {
            var loadBalancer1 = networkManager.LoadBalancers.Define(loadBalancerName1)
                                        .WithRegion(region)
                                        .WithExistingResourceGroup(resourceGroup)
                                        // Add two rules that uses above backend and probe
                                        .DefineLoadBalancingRule(httpLoadBalancingRule)
                                            .WithProtocol(TransportProtocol.Tcp)
                                            .FromFrontend(frontendName)
                                            .FromFrontendPort(80)
                                            .ToBackend(backendPoolName1)
                                            .WithProbe(httpProbe)
                                            .Attach()
                                        .DefineLoadBalancingRule(fabricGatewayLoadBalancingRule)
                                            .WithProtocol(TransportProtocol.Tcp)
                                            .FromFrontend(frontendName)
                                            .FromFrontendPort(19000)
                                            .ToBackend(backendPoolName1)
                                            .WithProbe(fabricGatewayProbe)
                                            .Attach()
                                        .DefineLoadBalancingRule(fabricHttpGatewayLoadBalancingRule)
                                            .WithProtocol(TransportProtocol.Tcp)
                                            .FromFrontend(frontendName)
                                            .FromFrontendPort(19080)
                                            .ToBackend(backendPoolName1)
                                            .WithProbe(fabricHttpGatewayProbe)
                                            .Attach()
                                        // Add nat pools to enable direct VM connectivity for
                                        .DefineInboundNatPool(rdpNatPool)
                                            .WithProtocol(TransportProtocol.Tcp)
                                            .FromFrontend(frontendName)
                                            .FromFrontendPortRange(3389, 4600)
                                            .ToBackendPort(3389)
                                            .Attach()
                                        .DefinePublicFrontend(frontendName)
                                            .WithExistingPublicIPAddress(publicIPAddress)
                                            .Attach()
                                        // Add health probes
                                        .DefineHttpProbe(httpProbe)
                                            .WithRequestPath("/")
                                            .WithPort(80)
                                            .Attach()
                                        .DefineTcpProbe(fabricGatewayProbe)
                                            .WithPort(19000)
                                            .WithIntervalInSeconds(5)
                                            .WithNumberOfProbes(2)
                                            .Attach()
                                        .DefineTcpProbe(fabricHttpGatewayProbe)
                                            .WithPort(19080)
                                            .WithIntervalInSeconds(5)
                                            .WithNumberOfProbes(2)
                                            .Attach()
                                        .WithSku(LoadBalancerSkuType.Standard)
                                        .Create();
            return loadBalancer1;
        }

        private static IVirtualMachineScaleSet CreateScaleSet(Region region, string backendPoolName1, string vmssName, string rdpNatPool, string userName, string password, string subnetName, IComputeManager computeManager, IResourceGroup resourceGroup, IStorageAccount storageAccountDiagnostics, INetwork network, ILoadBalancer loadBalancer1, string thumbprint, IVault vault, string secretIdentifier, string nodeTypeName, string clusterEndpoint)
        {
            var scaleSet = computeManager.VirtualMachineScaleSets.Define(vmssName)
                                    .WithRegion(region)
                                    .WithExistingResourceGroup(resourceGroup)
                                    .WithSku(VirtualMachineScaleSetSkuTypes.StandardD2v2)
                                    .WithExistingPrimaryNetworkSubnet(network, subnetName)
                                    .WithExistingPrimaryInternetFacingLoadBalancer(loadBalancer1)
                                    .WithPrimaryInternetFacingLoadBalancerBackends(backendPoolName1)
                                    .WithPrimaryInternetFacingLoadBalancerInboundNatPools(rdpNatPool)
                                    .WithoutPrimaryInternalLoadBalancer()
                                    .WithLatestWindowsImage("MicrosoftWindowsServer", "WindowsServer", "2019-Datacenter-with-Containers")
                                    .WithAdminUsername(userName)
                                    .WithAdminPassword(password)
                                    .WithComputerNamePrefix(nodeTypeName)
                                    .WithVaultSecret(vault.Id, secretIdentifier, "My")
                                    .WithOverProvision(false)
                                    .WithUpgradeMode(Microsoft.Azure.Management.Compute.Fluent.Models.UpgradeMode.Automatic)
                                    .WithCapacity(5)
                                    .WithVirtualMachinePublicIp()
                                    .DefineNewExtension("ServiceFabric")
                                        .WithPublisher("Microsoft.Azure.ServiceFabric")
                                        .WithType("ServiceFabricNode")
                                        .WithVersion("1.1")
                                        .WithMinorVersionAutoUpgrade()
                                        .WithProtectedSetting("StorageAccountKey1", storageAccountDiagnostics.GetKeys()[0].Value)
                                        .WithProtectedSetting("StorageAccountKey2", storageAccountDiagnostics.GetKeys()[1].Value)
                                        .WithPublicSetting("clusterEndpoint", clusterEndpoint)
                                        .WithPublicSetting("nodeTypeRef", nodeTypeName)
                                        .WithPublicSetting("dataPath", "D:\\\\SvcFab")
                                        .WithPublicSetting("durabilityLevel", "Silver")
                                        .WithPublicSetting("enableParallelJobs", true)
                                        .WithPublicSetting("nicPrefixOverride", network.Subnets[subnetName].AddressPrefix)
                                        .WithPublicSetting("certificate", new Dictionary<string, string>() { { "thumbprint", thumbprint }, { "x509StoreName", "My" } })
                                        .Attach()
                                    .Create();

            return scaleSet;
        }

        private static IStorageAccount CreateStorageAccount(string name, Region region, IStorageManager storageManager, IResourceGroup resourceGroup)
        {
            return storageManager.StorageAccounts.Define(name)
                .WithRegion(region)
                .WithExistingResourceGroup(resourceGroup)
                .WithSku(StorageAccountSkuType.Standard_LRS)
                .Create();
        }

        private static IVault CreateKeyVault(Region region, string vaultName, IKeyVaultManager keyVaultManager, IResourceGroup resourceGroup)
        {
            var spnCredentialsClientId = HttpMockServer.Variables[ConnectionStringKeys.ServicePrincipalKey];

            var vault1 = keyVaultManager.Vaults
                                    .Define(vaultName)
                                    .WithRegion(region)
                                    .WithExistingResourceGroup(resourceGroup)
                                    .DefineAccessPolicy()
                                        .ForServicePrincipal(spnCredentialsClientId)
                                        .AllowKeyAllPermissions()
                                        .AllowSecretAllPermissions()
                                        .AllowCertificateAllPermissions()
                                        .Attach()
                                    .WithDeploymentEnabled()
                                    .Create();

            return vault1;
        }

        private static IResourceGroup CreateResourceGroup(Region region, string resourceGroupName, IResourceManager resourceManager)
        {
            return resourceManager.ResourceGroups
                                    .Define(resourceGroupName)
                                    .WithRegion(region)
                                    .Create();
        }

        private static ServicePrincipalLoginInformation ParseAuthFile(string authFile)
        {
            var info = new ServicePrincipalLoginInformation();

            var lines = File.ReadLines(authFile);
            if (lines.First().Trim().StartsWith("{"))
            {
                string json = string.Join("", lines);
                var jsonConfig = Microsoft.Rest.Serialization.SafeJsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                info.ClientId = jsonConfig["clientId"];
                if (jsonConfig.ContainsKey("clientSecret"))
                {
                    info.ClientSecret = jsonConfig["clientSecret"];
                }
            }
            else
            {
                lines.All(line =>
                {
                    if (line.Trim().StartsWith("#"))
                        return true; // Ignore comments
                    var keyVal = line.Trim().Split(new char[] { '=' }, 2);
                    if (keyVal.Length < 2)
                        return true; // Ignore lines that don't look like $$$=$$$
                    if (keyVal[0].Equals("client", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ClientId = keyVal[1];
                    }
                    if (keyVal[0].Equals("key", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ClientSecret = keyVal[1];
                    }
                    return true;
                });
            }

            return info;
        }

        private static SecretBundle CreateCertificate(string clusterDnsName, IKeyVaultManager keyVaultManager, IVault vault1)
        {
            var servicePrincipalInfo = ParseAuthFile(System.Environment.GetEnvironmentVariable("AZURE_AUTH_LOCATION"));
            var keyVaultClient = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(async (authority, resource, scope) =>
            {
                var context = new AuthenticationContext(authority, TokenCache.DefaultShared);
                var result = await context.AcquireTokenAsync(resource, new ClientCredential(servicePrincipalInfo.ClientId, servicePrincipalInfo.ClientSecret));
                return result.AccessToken;
            }), ((KeyVaultManagementClient)keyVaultManager.Vaults.Manager.Inner).HttpClient);

            string certName = clusterDnsName.Split('.')[0];

            var certificateOperation = keyVaultClient.CreateCertificateAsync(
                vault1.VaultUri,
                certName,
                new CertificatePolicy()
                {
                    SecretProperties = new SecretProperties("application/x-pkcs12"),
                    X509CertificateProperties = new X509CertificateProperties()
                    {
                        Subject = "cn=" + clusterDnsName,
                        Ekus = new List<string> { "1.3.6.1.5.5.7.3.1", "1.3.6.1.5.5.7.3.2" }
                    },
                    IssuerParameters = new IssuerParameters() { Name = "Self" }
                }
            ).Result;

            while (certificateOperation.Status == "inProgress")
            {
                Console.WriteLine($"Creation of certificate '{certName}' is in progress");
                Task.Delay(1000);
                certificateOperation = keyVaultClient.GetCertificateOperationAsync(vault1.VaultUri, certName).Result;
            }

            Console.WriteLine($"Creation of certificate '{certName}' is in status '{certificateOperation.Status}'");

            return keyVaultClient.GetSecretAsync(vault1.VaultUri, certName).Result;
        }
    }
}
