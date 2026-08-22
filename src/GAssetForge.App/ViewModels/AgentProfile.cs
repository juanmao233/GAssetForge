using System;
using System.Collections.Generic;
using GAssetForge.Core;

namespace GAssetForge.App.ViewModels;

public sealed class AgentProfile
{
    private readonly Func<Uri, string> _configSnippetFactory;

    private AgentProfile(
        string key,
        string displayName,
        string description,
        bool isRuntimeAvailable,
        Func<Uri, string> configSnippetFactory)
    {
        Key = key;
        DisplayName = displayName;
        Description = description;
        IsRuntimeAvailable = isRuntimeAvailable;
        _configSnippetFactory = configSnippetFactory;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public bool IsRuntimeAvailable { get; }

    public string CreateConfigSnippet(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return _configSnippetFactory(endpoint);
    }

    public override string ToString() => DisplayName;

    public static IReadOnlyList<AgentProfile> Supported { get; } =
    [
        new AgentProfile(
            "codex",
            "Codex",
            "由 GAssetForge 管理 Codex App Server 会话，并在不兼容时降级到 codex exec。",
            true,
            endpoint =>
                $"[mcp_servers.{ProductIdentity.McpServerName}]{Environment.NewLine}" +
                $"url = \"{endpoint.AbsoluteUri}\""),
        new AgentProfile(
            "opencode",
            "OpenCode",
            "预留 OpenCode ACP Adapter；当前环境尚未安装或接入持久会话运行时。",
            false,
            endpoint => $$"""
                {
                  "$schema": "https://opencode.ai/config.json",
                  "mcp": {
                    "{{ProductIdentity.OpenCodeMcpServerName}}": {
                      "type": "remote",
                      "url": "{{endpoint.AbsoluteUri}}",
                      "enabled": true
                    }
                  }
                }
                """)
    ];

    public static AgentProfile Find(string? key)
    {
        foreach (var profile in Supported)
        {
            if (string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return Supported[0];
    }
}
