## Summary

Describe the user-visible outcome and the module or workflow affected.

## Verification

- [ ] `dotnet restore .\GAssetForge.sln --locked-mode`
- [ ] `dotnet build .\GAssetForge.sln -c Release --no-restore`
- [ ] Smoke runner passes
- [ ] No generated assets, local data, secrets, `dist/`, or `artifacts/` were added

## Compatibility

Call out changes to project manifests, LocalAppData, MCP identifiers, task contracts, or generated asset paths.
