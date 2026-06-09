# OSS Mirror Migration Plan

本分支目标是基于官方 Windows Node 安装器做中文适配，并把安装时需要访问的外部依赖迁移到可控的 OSS/CDN。

## 当前实现范围

- 新增 `SetupConfig.Oss` 配置段。
- 支持通过本地或 HTTPS manifest 解析 CLI 安装脚本 URL，并在 Windows 端先下载、校验 `sha256`/`size` 后再通过 WSL stdin 执行。
- 支持从同一个 manifest 解析 Ubuntu 24.04 WSL rootfs，下载到本地缓存、校验 `sha256`/`size` 后通过 `wsl --import ... --version 2` 创建网关实例。
- 支持从同一个 manifest 解析 Microsoft WSL x64/arm64 MSI。缺少 WSL 平台时先下载到本地缓存、校验 `sha256`/`size`，再通过提权 `msiexec /i ... /qn /norestart` 安装。
- `Gateway.InstallUrl` 仍然优先级最高，便于临时调试和灰度。
- OSS manifest 不可用时默认回退官方 URL、`wsl.exe --install --no-distribution` 或 `wsl.exe --install --web-download`；可通过 `AllowOfficialFallback=false` 改为直接失败。

当前 manifest 示例：

```json
{
  "schemaVersion": 1,
  "version": "2026.6.1-cn.1",
  "installCli": {
    "url": "https://example-oss.example.com/openclaw/install-cli.sh",
    "sha256": "64-char-sha256-hex",
    "size": 12345,
    "environment": {
      "OPENCLAW_NODE_DIST_BASE_URL": "https://example-oss.example.com/node/dist",
      "NPM_CONFIG_REGISTRY": "https://registry.npmmirror.com",
      "COREPACK_NPM_REGISTRY": "https://registry.npmmirror.com"
    }
  },
  "ubuntuWslRootfs": {
    "url": "https://example-oss.example.com/wsl/ubuntu-24.04/rootfs.tar.gz",
    "sha256": "64-char-sha256-hex",
    "size": 123456789,
    "baseDistro": "Ubuntu-24.04",
    "architecture": "amd64"
  },
  "wslCore": {
    "version": "2.7.3",
    "assets": {
      "x64": {
        "url": "https://example-oss.example.com/wsl/core/2.7.3/wsl.2.7.3.0.x64.msi",
        "sha256": "64-char-sha256-hex",
        "size": 255135744
      },
      "arm64": {
        "url": "https://example-oss.example.com/wsl/core/2.7.3/wsl.2.7.3.0.arm64.msi",
        "sha256": "64-char-sha256-hex",
        "size": 254001152
      }
    }
  }
}
```

## 配置方式

`default-config.json`：

```json
{
  "Oss": {
    "Enabled": true,
    "ManifestUrl": "https://example-oss.example.com/openclaw/dependencies.json",
    "ManifestPath": null,
    "AllowOfficialFallback": true
  }
}
```

环境变量：

- `OPENCLAW_SETUP_OSS_ENABLED=true`
- `OPENCLAW_SETUP_OSS_MANIFEST_URL=https://example-oss.example.com/openclaw/dependencies.json`
- `OPENCLAW_SETUP_OSS_MANIFEST_PATH=C:\path\dependencies.json`
- `OPENCLAW_SETUP_OSS_ALLOW_OFFICIAL_FALLBACK=false`

## 后续批次

1. 梳理 `install-cli.sh` 内部二次下载的资源，并把 OpenClaw npm 包、模型或语音资源加入 manifest。
2. 为 Setup Engine 控制台输出、失败提示、向导动态内容建立中文资源文件。
3. 增加“国内网络模式”预检，提前检查 OSS manifest、WSL、端口、权限和回退策略。
