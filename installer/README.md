# Timbre MSI installer

This WiX v4 project builds an x64 `.msi` for the unpackaged WinUI 3 app.

## Build

1. Build the installer:

   ```powershell
   dotnet build .\installer\timbre.installer.wixproj -c Release
   ```

   Or build a newer upgradeable MSI version explicitly:

   ```powershell
   dotnet build .\installer\timbre.installer.wixproj -c Release -p:PackageVersion=1.0.1
   ```

2. The installer project publishes the app first, then packages the published files into an MSI.

## Output

- App publish output: `timbre\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\`
- MSI output: `installer\bin\Release\timbre.installer.msi`

## Notes

- The installer uses the standard WiX install-directory wizard UI.
- The default install location is `Program Files\Timbre`.
- A Start Menu shortcut is created automatically.
- To upgrade an existing install, build a newer `PackageVersion` than the one already installed.
- The installer packages the working `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\` app output, which includes the generated `.xbf` and `.pri` files required by WinUI.
