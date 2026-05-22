using System.Linq;
using Pulumi;
using Oci = Pulumi.Oci;

namespace YARG.Online.Infrastructure;

/// <summary>Outputs from provisioning the OCI/OKE environment that the rest of the program needs.</summary>
public sealed record OracleResources(
    Output<string> KubeConfig,
    Output<string> NlbSubnetId,
    Output<string> NlbNsgId,
    Output<string> NlbReservedIp,
    Output<string> ClusterId,
    Output<string> LobbiesRepositoryId,
    Output<string> GameRepositoryId);

/// <summary>
/// Provisions the OCI side of the deployment: VCN + networking, an OKE cluster, three
/// Ampere A1 node pools, OCIR repositories, and the IAM grant plus node cloud-init
/// (the OKE image credential provider) that let worker nodes pull from OCIR without
/// an image-pull secret.
/// </summary>
public static class Oracle
{
    public static OracleResources Provision(Config config)
    {
        // Reuse the OCI provider's own `oci:tenancyOcid` setting for the dynamic-group /
        // policy compartment and the availability-domain lookup, rather than duplicating
        // it under a bespoke key. There is no provider-level compartment, so
        // `compartmentId` is necessarily an application config value.
        var tenancyId = new Pulumi.Config("oci").Require("tenancyOcid");
        var compartmentId = config.Require("compartmentId");
        var vcnCidr = config.Get("vcnCidr") ?? "10.0.0.0/16";
        var kubernetesVersion = config.Get("kubernetesVersion") ?? "v1.35.2";
        var sshPublicKey = config.Get("sshPublicKey");
        var imageIdOverride = config.Get("nodeImageId");

        // Always Free is single-AD — just take the first one.
        var availabilityDomain = Oci.Identity.GetAvailabilityDomains.Invoke(new()
        {
            CompartmentId = tenancyId,
        }).Apply(r => r.AvailabilityDomains[0].Name!);

        // --- VCN & gateways ---
        var vcn = new Oci.Core.Vcn("yarg-vcn", new()
        {
            CompartmentId = compartmentId,
            CidrBlocks = { vcnCidr },
            DisplayName = "yarg-online",
            DnsLabel = "yarg",
        });

        var internetGateway = new Oci.Core.InternetGateway("yarg-igw", new()
        {
            CompartmentId = compartmentId,
            VcnId = vcn.Id,
            DisplayName = "yarg-igw",
            Enabled = true,
        });

        var natGateway = new Oci.Core.NatGateway("yarg-nat", new()
        {
            CompartmentId = compartmentId,
            VcnId = vcn.Id,
            DisplayName = "yarg-nat",
        });

        // The "all <region> services" entry — used by the service gateway and its route rule.
        var ociServices = Oci.Core.GetServices.Invoke();
        var allServicesId = ociServices.Apply(r =>
            r.Services.First(s => s.CidrBlock.StartsWith("all-")).Id);
        var allServicesCidr = ociServices.Apply(r =>
            r.Services.First(s => s.CidrBlock.StartsWith("all-")).CidrBlock);

        var serviceGateway = new Oci.Core.ServiceGateway("yarg-sgw", new()
        {
            CompartmentId = compartmentId,
            VcnId = vcn.Id,
            DisplayName = "yarg-sgw",
            Services =
            {
                new Oci.Core.Inputs.ServiceGatewayServiceArgs { ServiceId = allServicesId },
            },
        });

        // --- Route tables ---
        var publicRouteTable = new Oci.Core.RouteTable("yarg-rt-public", new()
        {
            CompartmentId = compartmentId,
            VcnId = vcn.Id,
            DisplayName = "yarg-rt-public",
            RouteRules =
            {
                new Oci.Core.Inputs.RouteTableRouteRuleArgs
                {
                    Destination = "0.0.0.0/0",
                    DestinationType = "CIDR_BLOCK",
                    NetworkEntityId = internetGateway.Id,
                },
            },
        });

        var privateRouteTable = new Oci.Core.RouteTable("yarg-rt-private", new()
        {
            CompartmentId = compartmentId,
            VcnId = vcn.Id,
            DisplayName = "yarg-rt-private",
            RouteRules =
            {
                new Oci.Core.Inputs.RouteTableRouteRuleArgs
                {
                    Destination = "0.0.0.0/0",
                    DestinationType = "CIDR_BLOCK",
                    NetworkEntityId = natGateway.Id,
                },
                new Oci.Core.Inputs.RouteTableRouteRuleArgs
                {
                    Destination = allServicesCidr,
                    DestinationType = "SERVICE_CIDR_BLOCK",
                    NetworkEntityId = serviceGateway.Id,
                },
            },
        });

        // --- Network security groups ---
        var workerNsg = new Oci.Core.NetworkSecurityGroup("yarg-nsg-worker", new()
        { CompartmentId = compartmentId, VcnId = vcn.Id, DisplayName = "yarg-nsg-worker" });
        var gameserverNsg = new Oci.Core.NetworkSecurityGroup("yarg-nsg-gameserver", new()
        { CompartmentId = compartmentId, VcnId = vcn.Id, DisplayName = "yarg-nsg-gameserver" });
        var apiNsg = new Oci.Core.NetworkSecurityGroup("yarg-nsg-api", new()
        { CompartmentId = compartmentId, VcnId = vcn.Id, DisplayName = "yarg-nsg-api" });
        var nlbNsg = new Oci.Core.NetworkSecurityGroup("yarg-nsg-nlb", new()
        { CompartmentId = compartmentId, VcnId = vcn.Id, DisplayName = "yarg-nsg-nlb" });

        void Rule(string name, Input<string> nsgId, string direction, string protocol,
                  string cidr, int? portMin = null, int? portMax = null)
        {
            var args = new Oci.Core.NetworkSecurityGroupSecurityRuleArgs
            {
                NetworkSecurityGroupId = nsgId,
                Direction = direction,
                Protocol = protocol,
            };
            if (direction == "INGRESS")
            {
                args.Source = cidr;
                args.SourceType = "CIDR_BLOCK";
            }
            else
            {
                args.Destination = cidr;
                args.DestinationType = "CIDR_BLOCK";
            }
            if (protocol == "6" && portMin.HasValue)
            {
                args.TcpOptions = new Oci.Core.Inputs.NetworkSecurityGroupSecurityRuleTcpOptionsArgs
                {
                    DestinationPortRange = new Oci.Core.Inputs.NetworkSecurityGroupSecurityRuleTcpOptionsDestinationPortRangeArgs
                    { Min = portMin.Value, Max = portMax ?? portMin.Value },
                };
            }
            if (protocol == "17" && portMin.HasValue)
            {
                args.UdpOptions = new Oci.Core.Inputs.NetworkSecurityGroupSecurityRuleUdpOptionsArgs
                {
                    DestinationPortRange = new Oci.Core.Inputs.NetworkSecurityGroupSecurityRuleUdpOptionsDestinationPortRangeArgs
                    { Min = portMin.Value, Max = portMax ?? portMin.Value },
                };
            }
            _ = new Oci.Core.NetworkSecurityGroupSecurityRule(name, args);
        }

        // Intra-VCN traffic is allowed wholesale (node-to-node, control plane <-> nodes);
        // only the world-facing rules are specific.
        Rule("worker-in-vcn", workerNsg.Id, "INGRESS", "all", vcnCidr);
        Rule("worker-eg-all", workerNsg.Id, "EGRESS", "all", "0.0.0.0/0");

        Rule("gs-in-vcn", gameserverNsg.Id, "INGRESS", "all", vcnCidr);
        Rule("gs-in-udp", gameserverNsg.Id, "INGRESS", "17", "0.0.0.0/0", 7000, 8000);
        Rule("gs-eg-all", gameserverNsg.Id, "EGRESS", "all", "0.0.0.0/0");

        Rule("api-in-vcn", apiNsg.Id, "INGRESS", "all", vcnCidr);
        Rule("api-in-6443", apiNsg.Id, "INGRESS", "6", "0.0.0.0/0", 6443);
        Rule("api-eg-all", apiNsg.Id, "EGRESS", "all", "0.0.0.0/0");

        // Cloudflare-IP allowlist is a follow-up; for now 80/443 are open to the world.
        Rule("nlb-in-80", nlbNsg.Id, "INGRESS", "6", "0.0.0.0/0", 80);
        Rule("nlb-in-443", nlbNsg.Id, "INGRESS", "6", "0.0.0.0/0", 443);
        Rule("nlb-eg-all", nlbNsg.Id, "EGRESS", "all", "0.0.0.0/0");

        // --- Subnets (regional) ---
        Oci.Core.Subnet Subnet(string name, string cidr, string dnsLabel,
                               Input<string> routeTableId, bool prohibitPublicIp) =>
            new(name, new Oci.Core.SubnetArgs
            {
                CompartmentId = compartmentId,
                VcnId = vcn.Id,
                CidrBlock = cidr,
                DisplayName = name,
                DnsLabel = dnsLabel,
                RouteTableId = routeTableId,
                ProhibitPublicIpOnVnic = prohibitPublicIp,
            });

        var apiSubnet = Subnet("yarg-subnet-api", "10.0.0.0/28", "api", publicRouteTable.Id, false);
        var nlbSubnet = Subnet("yarg-subnet-nlb", "10.0.10.0/24", "nlb", publicRouteTable.Id, false);
        var workerPrivateSubnet = Subnet("yarg-subnet-wpriv", "10.0.1.0/24", "wpriv", privateRouteTable.Id, true);
        var workerPublicSubnet = Subnet("yarg-subnet-wpub", "10.0.2.0/24", "wpub", publicRouteTable.Id, false);

        // Reserved public IP for the NLB — known up-front, so the cluster provisions in
        // a single pass and external-dns has a stable target. The OKE cloud-controller
        // attaches it to the NLB via the `oci.oraclecloud.com/reserved-ips` annotation.
        var nlbReservedIp = new Oci.Core.PublicIp("yarg-nlb-ip", new()
        {
            CompartmentId = compartmentId,
            Lifetime = "RESERVED",
            DisplayName = "yarg-online-nlb",
        });

        // --- OKE cluster ---
        var cluster = new Oci.ContainerEngine.Cluster("yarg-oke", new()
        {
            CompartmentId = compartmentId,
            Name = "yarg-online",
            KubernetesVersion = kubernetesVersion,
            VcnId = vcn.Id,
            Type = "BASIC_CLUSTER",
            EndpointConfig = new Oci.ContainerEngine.Inputs.ClusterEndpointConfigArgs
            {
                SubnetId = apiSubnet.Id,
                IsPublicIpEnabled = true,
                NsgIds = { apiNsg.Id },
            },
            Options = new Oci.ContainerEngine.Inputs.ClusterOptionsArgs
            {
                ServiceLbSubnetIds = { nlbSubnet.Id },
                KubernetesNetworkConfig = new Oci.ContainerEngine.Inputs.ClusterOptionsKubernetesNetworkConfigArgs
                {
                    PodsCidr = "10.244.0.0/16",
                    ServicesCidr = "10.96.0.0/16",
                },
            },
        });

        // --- Worker node image: OKE OL8 aarch64 for K8s 1.35 (OL7 is unsupported) ---
        var imageId = imageIdOverride is not null
            ? Output.Create(imageIdOverride)
            : Oci.ContainerEngine.GetNodePoolOption.Invoke(new()
            {
                NodePoolOptionId = "all",
                CompartmentId = compartmentId,
            }).Apply(o =>
            {
                var v = kubernetesVersion.TrimStart('v');
                var match = o.Sources.FirstOrDefault(s =>
                        s.SourceName.Contains("aarch64") &&
                        s.SourceName.Contains("Oracle-Linux-8") &&
                        s.SourceName.Contains("OKE-" + v))
                    ?? o.Sources.First(s => s.SourceName.Contains("aarch64"));
                return match.ImageId;
            });

        // The OKE Image Credential Provider for OCIR lets the kubelet pull private
        // OCIR images using each worker node's instance principal — no image-pull
        // secret. This custom cloud-init replaces OKE's default node bootstrap, so
        // it fetches and runs the standard oke-init.sh itself. The binary is arm64
        // to match the Ampere A1 node pools; CRLF is normalised to LF so the script
        // stays valid however this file is saved.
        var credentialProviderCloudInit =
            """
            #!/bin/bash
            curl --fail -H "Authorization: Bearer Oracle" -L0 http://169.254.169.254/opc/v2/instance/metadata/oke_init_script | base64 --decode >/var/run/oke-init.sh

            wget https://github.com/oracle-devrel/oke-credential-provider-for-ocir/releases/latest/download/oke-credential-provider-for-ocir-linux-arm64 -O /usr/local/bin/credential-provider-oke
            wget https://github.com/oracle-devrel/oke-credential-provider-for-ocir/releases/latest/download/credential-provider-config.yaml -P /etc/kubernetes/

            sudo chmod 755 /usr/local/bin/credential-provider-oke

            bash /var/run/oke-init.sh --kubelet-extra-args "--image-credential-provider-bin-dir=/usr/local/bin/ --image-credential-provider-config=/etc/kubernetes/credential-provider-config.yaml"
            """.Replace("\r\n", "\n");
        var nodeUserData = System.Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(credentialProviderCloudInit));

        // --- Node pools (all Ampere VM.Standard.A1.Flex) ---
        Oci.ContainerEngine.NodePool Pool(string name, double ocpus, double memGb,
                                          Input<string> subnetId, Input<string> nsgId, string label)
        {
            var args = new Oci.ContainerEngine.NodePoolArgs
            {
                ClusterId = cluster.Id,
                CompartmentId = compartmentId,
                Name = name,
                KubernetesVersion = kubernetesVersion,
                NodeShape = "VM.Standard.A1.Flex",
                NodeShapeConfig = new Oci.ContainerEngine.Inputs.NodePoolNodeShapeConfigArgs
                {
                    Ocpus = ocpus,
                    MemoryInGbs = memGb,
                },
                NodeSourceDetails = new Oci.ContainerEngine.Inputs.NodePoolNodeSourceDetailsArgs
                {
                    SourceType = "IMAGE",
                    ImageId = imageId,
                },
                NodeConfigDetails = new Oci.ContainerEngine.Inputs.NodePoolNodeConfigDetailsArgs
                {
                    Size = 1,
                    PlacementConfigs =
                    {
                        new Oci.ContainerEngine.Inputs.NodePoolNodeConfigDetailsPlacementConfigArgs
                        {
                            AvailabilityDomain = availabilityDomain,
                            SubnetId = subnetId,
                        },
                    },
                    NsgIds = { nsgId },
                    NodePoolPodNetworkOptionDetails = new Oci.ContainerEngine.Inputs.NodePoolNodeConfigDetailsNodePoolPodNetworkOptionDetailsArgs
                    {
                        CniType = "FLANNEL_OVERLAY",
                    },
                },
                InitialNodeLabels =
                {
                    new Oci.ContainerEngine.Inputs.NodePoolInitialNodeLabelArgs
                    { Key = "workload", Value = label },
                },
                // Custom cloud-init installing the OCIR image credential provider.
                NodeMetadata =
                {
                    { "user_data", nodeUserData },
                },
            };
            if (sshPublicKey is not null)
                args.SshPublicKey = sshPublicKey;
            return new Oci.ContainerEngine.NodePool(name, args);
        }

        _ = Pool("system", 2, 12, workerPrivateSubnet.Id, workerNsg.Id, "system");
        _ = Pool("gameserver", 1, 2, workerPublicSubnet.Id, gameserverNsg.Id, "gameserver");
        _ = Pool("services", 1, 6, workerPrivateSubnet.Id, workerNsg.Id, "services");

        // --- OCIR repositories ---
        var lobbiesRepo = new Oci.Artifacts.ContainerRepository("yarg-repo-lobbies", new()
        {
            CompartmentId = compartmentId,
            DisplayName = "yarg-online/lobbies",
            IsPublic = false,
        });
        var gameRepo = new Oci.Artifacts.ContainerRepository("yarg-repo-game", new()
        {
            CompartmentId = compartmentId,
            DisplayName = "yarg-online/game",
            IsPublic = false,
        });

        // --- IAM: passwordless OCIR pulls for worker nodes (instance principal) ---
        var nodeDynamicGroup = new Oci.Identity.DynamicGroup("yarg-oke-nodes", new()
        {
            CompartmentId = tenancyId,
            Name = "yarg-oke-worker-nodes",
            Description = "YARG.Online OKE worker node instances",
            MatchingRule = $"ALL {{instance.compartment.id = '{compartmentId}'}}",
        });

        _ = new Oci.Identity.Policy("yarg-ocir-pull", new()
        {
            CompartmentId = tenancyId,
            Name = "yarg-oke-ocir-pull",
            Description = "Allow YARG.Online OKE worker nodes to pull images from OCIR",
            Statements =
            {
                nodeDynamicGroup.Id.Apply(id =>
                    $"Allow dynamic-group id {id} to read repos in tenancy"),
            },
        });

        var kubeConfig = Oci.ContainerEngine.GetClusterKubeConfig.Invoke(new()
        {
            ClusterId = cluster.Id,
        }).Apply(k => k.Content);

        return new OracleResources(
            kubeConfig,
            nlbSubnet.Id,
            nlbNsg.Id,
            nlbReservedIp.IpAddress,
            cluster.Id,
            lobbiesRepo.Id,
            gameRepo.Id);
    }
}
