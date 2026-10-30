using System.Collections.Generic;
using HarmonyLib;
using Terraform.Configs;

namespace Terraform.Network;

public static class ServerSync {
    private const string HandshakeRpc = "Terraform_Handshake";
    private const string ConfigRpc    = "Terraform_Config";

    private static readonly HashSet<ZRpc> ValidatedPeers = new();
    private static bool _serverConfirmed;
    private static float _serverBrushSize;
    private static float _serverStonePerCubicMeter;

    private static bool IsRemoteClient => ZNet.instance != null && !ZNet.instance.IsServer();

    public static bool ServerRunsTerraform =>ZNet.instance != null && (ZNet.instance.IsServer() || _serverConfirmed);

    public static float BrushSize =>
        IsRemoteClient && _serverConfirmed ? _serverBrushSize : ModConfig.BrushSize.Value;

    public static float StonePerCubicMeter =>
        IsRemoteClient && _serverConfirmed ? _serverStonePerCubicMeter : ModConfig.StonePerCubicMeter.Value;

    public static void Init() {
        ModConfig.BrushSize.SettingChanged          += (_, _) => BroadcastConfig();
        ModConfig.StonePerCubicMeter.SettingChanged += (_, _) => BroadcastConfig();
    }

    private static ZPackage VersionPackage() {
        var pkg = new ZPackage();
        pkg.Write(TerraformPlugin.Version);
        return pkg;
    }

    private static ZPackage ConfigPackage() {
        var pkg = new ZPackage();
        pkg.Write(ModConfig.BrushSize.Value);
        pkg.Write(ModConfig.StonePerCubicMeter.Value);
        return pkg;
    }

    private static void BroadcastConfig() {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
        foreach (var peer in ZNet.instance.GetPeers())
            if (peer.m_rpc != null && ValidatedPeers.Contains(peer.m_rpc)) peer.m_rpc.Invoke(ConfigRpc, ConfigPackage());
    }

    internal static void OnNewConnection(ZNet net, ZNetPeer peer) {
        if (net.IsServer()) {
            peer.m_rpc.Register<ZPackage>(HandshakeRpc, RPC_ClientHandshake);
            return;
        }
        _serverConfirmed = false;
        peer.m_rpc.Register<ZPackage>(HandshakeRpc, RPC_ServerHandshake);
        peer.m_rpc.Register<ZPackage>(ConfigRpc, RPC_Config);
        peer.m_rpc.Invoke(HandshakeRpc, VersionPackage());
    }

    internal static bool AcceptPeerInfo(ZNet net, ZRpc rpc) {
        if (!net.IsServer() || ValidatedPeers.Contains(rpc)) return true;
        TerraformPlugin.Log.LogWarning($"[net] rejecting {rpc.GetSocket().GetEndPointString()}: Terraform {TerraformPlugin.Version} missing or mismatched on the client");
        rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
        return false;
    }

    internal static void OnDisconnect(ZNetPeer peer) {
        if (peer?.m_rpc != null) ValidatedPeers.Remove(peer.m_rpc);
    }

    private static void RPC_ClientHandshake(ZRpc rpc, ZPackage pkg) {
        string clientVersion = pkg.ReadString();
        if (clientVersion != TerraformPlugin.Version) {
            TerraformPlugin.Log.LogWarning($"[net] client Terraform {clientVersion} does not match server {TerraformPlugin.Version}");
            return;
        }
        ValidatedPeers.Add(rpc);
        rpc.Invoke(HandshakeRpc, VersionPackage());
        rpc.Invoke(ConfigRpc, ConfigPackage());
    }

    private static void RPC_ServerHandshake(ZRpc rpc, ZPackage pkg) {
        string serverVersion = pkg.ReadString();
        _serverConfirmed = serverVersion == TerraformPlugin.Version;
        if (!_serverConfirmed)
            TerraformPlugin.Log.LogError($"[net] server Terraform {serverVersion} does not match client {TerraformPlugin.Version}; digging disabled");
    }

    private static void RPC_Config(ZRpc rpc, ZPackage pkg) {
        _serverBrushSize          = pkg.ReadSingle();
        _serverStonePerCubicMeter = pkg.ReadSingle();
    }
}

[HarmonyPatch(typeof(ZNet), "OnNewConnection")]
public static class ZNet_OnNewConnection_Patch {
    static void Postfix(ZNet __instance, ZNetPeer peer) => ServerSync.OnNewConnection(__instance, peer);
}

[HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
public static class ZNet_RPC_PeerInfo_Patch {
    static bool Prefix(ZNet __instance, ZRpc rpc) => ServerSync.AcceptPeerInfo(__instance, rpc);
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
public static class ZNet_Disconnect_Patch {
    static void Prefix(ZNetPeer peer) => ServerSync.OnDisconnect(peer);
}
