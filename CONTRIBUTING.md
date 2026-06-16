# Contributing

Contributions are welcome! Here's how to get started.

## Reporting bugs

Open an [issue](https://github.com/geogengames-hue/MonitorAndControl/issues/new?template=bug_report.md) with:
- Steps to reproduce
- Expected vs actual behavior
- Windows version and logs from `%LOCALAPPDATA%\SystemHelper\monitor.log`

## Suggesting features

Open an [issue](https://github.com/geogengames-hue/MonitorAndControl/issues/new?template=feature_request.md) describing what you'd like to see and why.

## Submitting changes

1. Fork the repository
2. Create a branch: `git checkout -b my-feature`
3. Make your changes
4. Build and test locally:
   ```powershell
   dotnet build .\MonitorAndControl.csproj
   ```
5. Commit with a clear message
6. Push and open a Pull Request

## Guidelines

- Keep the existing code style (naming, braces, etc.)
- Add comments only where the logic is non-obvious
- If adding a new feature, update the README if applicable
- For new translations, add the JSON file to `Web/wwwroot/i18n/` and the `.resx` file to `Resources/`

## License

By contributing, you agree that your contributions will be licensed under the project's Fair Use License.
