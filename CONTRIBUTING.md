# Contributing to Olivia Charts

Bug reports, documentation improvements, and focused pull requests are welcome. You can write in English or Chinese.

## Report a problem or suggest a feature

Search the [existing issues](https://github.com/Reizk/Olivia.Charts/issues) first. For a bug, include the version, operating system, chart or demo page, steps to reproduce, and expected versus actual behavior. A small sample with synthetic data is especially helpful. Remove credentials and private data from attachments.

For a new chart type, API change, or substantial redesign, open a feature request before investing in a large implementation. Describe the intended use and how you would expect it to work.

Report security vulnerabilities privately using [SECURITY.md](SECURITY.md).

## Submit a change

1. Fork the repository and create a branch from `main`.
2. Follow the [developer guide](docs/DEVELOPMENT.md) to build and run the demo.
3. Keep the change focused. Preserve original sample values and identities when changing chart geometry or interaction.
4. Add a regression test for a behavior fix; for documentation-only changes, check the links and preview instead. For UI changes, include a screenshot and describe the interaction you checked.
5. Open a pull request explaining the problem, the resulting behavior, and the checks you ran.

Before submitting code, run:

```sh
dotnet restore Olivia.Charts.sln --locked-mode
dotnet build Olivia.Charts.sln -c Release --no-restore
dotnet test Olivia.Charts.sln -c Release --no-build
```

Use the existing C# style and `.editorconfig`. Keep package versions in `Directory.Packages.props`; update lock files intentionally when dependencies change. Do not include generated build outputs or user-specific IDE settings.

## License and community

By submitting a contribution, you agree to license it under the project's [MIT license](LICENSE). Submit only work you have the right to share, and preserve any third-party notices. Participation follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## 中文说明

欢迎提交问题、文档改进和范围明确的代码修改，可以使用中文交流。报告问题时请注明版本、系统、图表页面、复现步骤以及预期和实际结果；优先提供合成数据示例。较大的功能或 API 调整请先开 Issue 讨论。

代码修改请完成上述构建和相关测试，界面修改附上截图与操作说明。文档修改检查链接和显示即可。提交内容须有权公开，并采用本项目 MIT 许可证；安全问题请按 [安全政策](SECURITY.md) 私下报告。
