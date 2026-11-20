# 参与贡献

欢迎提 Issue、反馈应用兼容性（可用仓库自带的兼容性模板）或提交 PR。

## 开发环境与测试

- **系统与运行时**：Windows 10 / 11 + .NET 8 SDK
- 仓库不依赖第三方 NuGet 包，离线环境可直接构建和测试：

```powershell
dotnet build -c Release
dotnet run --project tests/KeyTrace.Tests -c Release
```

运行常驻程序 `keytrace run` 期间 exe 会被占用；重新编译前请先执行 `keytrace stop`。

## 提交与 PR 规范

1. **先讨论再大改**：较大改动先开 Issue 写清动机与验收标准。
2. **约定式提交**：如 `feat(export): ...`、`fix(attribute): ...`、`docs: ...`。
3. **测试先行**：核心逻辑（归因、导出、选词、练习回流）请在 `tests/KeyTrace.Tests/Program.cs` 补齐测试，PR 中附上 `dotnet run --project tests/KeyTrace.Tests -c Release` 的实际输出。
4. **隐私底线**：不要提交任何个人真实 `events.jsonl` / 含原文的标注文件；公开样本必须经过脱敏（`keytrace label --anonymize`）。

## 新增导出格式

导出层遵循「一种格式一个文件，不引入动态插件框架」的约定，具体步骤与字段要求见 [docs/export-formats.md](docs/export-formats.md)。
