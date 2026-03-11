# Map Viewer

Open [index.html](/c:/Users/vixti/source/repos/RMC-14/Tools/TacticalMapViewer/index.html) in a browser, then choose an exported map folder such as `Resources/MapImages/tacmap`.

Expected export folder contents:

- `manifest.json`
- `maps/*.json`
- `images/*.png`

Generate that data with:

```powershell
dotnet run --project .\Content.MapRenderer\Content.MapRenderer.csproj -- --tacmap
```

This viewer is standalone. It does not need to be copied into the export folder.
