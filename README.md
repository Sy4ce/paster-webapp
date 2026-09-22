# Paster · 临时文件寄存

一个单页面的临时文件分享服务：拖入一个文件，拿到一条取件链接，**五分钟后链接作废、文件从服务器删除**。

线上地址：<https://paster-sy4ce.azurewebsites.net>

## 它是怎么工作的

- 文件存在 Web App 自己的磁盘上（Linux 上是 `/home/data/paster`），一个上传对应两个文件：
  `<token>.bin` 存内容，`<token>.json` 存元数据（原始文件名、大小、到期时间）。
- `<token>` 是 16 字节随机数的 base64url 编码，22 个字符，猜不到。
- **到期判定以元数据为准**，发生在每一次查询/下载时：因此即使实例休眠、定时清理没跑，过期文件也拿不到。
  在此之上还有一个 20 秒一轮的后台清理任务负责真正释放磁盘。
- 取件链接在有效期内可以重复下载（同一链接多人取件不会被吞掉）。
- 下载一律以 `application/octet-stream` + `attachment` 返回，并带 `nosniff`：
  上传的 `.html` / `.svg` 不可能在本站域名下被浏览器渲染执行。

## 限制

| 项目 | 默认值 | 环境变量 |
| --- | --- | --- |
| 单文件上限 | 1 MiB | `PASTER_MAX_FILE_BYTES` |
| 总占用上限 | 64 MiB | `PASTER_MAX_TOTAL_BYTES` |
| 有效期 | 300 秒 | `PASTER_TTL_SECONDS` |
| 存储目录 | Linux 为 `/home/data/paster` | `PASTER_STORAGE_ROOT` |
| 链接基地址 | 由请求推断 | `PASTER_PUBLIC_BASE_URL` |

Azure 免费层（F1）总磁盘只有 1 GB，所以默认把占用卡在 64 MiB；超出的上传会拿到 `507` 并附带当前可用空间。

免费层没有 Always On，闲置约 20 分钟后实例会被卸载：下一次访问要等十几秒冷启动，页面会显示「正在唤醒服务器」。这是免费层的代价，不是故障。

## 本地运行

项目 target `net10.0`，所以本机需要 .NET 10 SDK（`winget install Microsoft.DotNet.SDK.10`）。只有 .NET 9 SDK 的话编译不过。

```bash
dotnet run                       # http://localhost:5000
PASTER_TTL_SECONDS=15 dotnet run # 想快速看链接作废，就把有效期调短
```

跑一遍冒烟测试（只依赖标准库）：

```bash
python tools/smoke.py http://localhost:5000
```

## 部署

推送 `main` 即自动部署，走 GitHub Actions + Azure OIDC 联合凭据，仓库里不存任何密码或发布配置文件。

```
push → dotnet publish → 本地冒烟测试 → az webapp deploy → 线上冒烟测试
```

Azure 侧的资源：资源组 `rg-paster`、Linux 应用服务计划 `asp-paster`（F1 免费层）、Web App `paster-sy4ce`（运行时 `DOTNETCORE:10.0`，启动命令 `dotnet Paster.dll`）。

需要的仓库密钥（Settings → Secrets and variables → Actions）：

| 名称 | 说明 |
| --- | --- |
| `AZURE_CLIENT_ID` | 联合凭据对应应用注册的 `appId` |
| `AZURE_TENANT_ID` | 租户 ID |
| `AZURE_SUBSCRIPTION_ID` | 订阅 ID |

联合凭据的 subject 绑定 main 分支，只有这个仓库的 main 分支能换到 Azure 的令牌。注意 GitHub 现在签发的 `sub` 里带不可变的数字 ID，所以应用注册上挂了两种格式，缺一个就会登录失败：

```
repo:Sy4ce/paster-webapp:ref:refs/heads/main
repo:Sy4ce@233271235/paster-webapp@1381933729:ref:refs/heads/main
```

排查办法：登录失败时看 Actions 日志里 `Federated token details` 打印的实际 `subject claim`，把它照抄成一条新的联合凭据即可。

### 已经建好的资源

| 资源 | 名称 / ID |
| --- | --- |
| 资源组 | `rg-paster`（East Asia） |
| 应用服务计划 | `asp-paster`（Linux F1 免费层，1 个实例） |
| Web App | `paster-sy4ce` → <https://paster-sy4ce.azurewebsites.net> |
| 应用注册 | `paster-webapp-github`，client id `4338cf72-8d9b-4e4e-baf0-88b1b814b388` |
| 服务主体角色 | Website Contributor，范围仅 `rg-paster` |
| 租户 / 订阅 | `e3e2b52c-1261-4b8f-9489-ac1c462346d6` / `6632b65e-1d9f-43df-96c9-cc57ebbb8fdc` |

client id、tenant id、subscription id 都不是密钥，可以公开；真正的凭据是每次运行由 GitHub 签发的短期 OIDC 令牌。

### 常用运维命令

```bash
# 改配置（例如把单文件上限提到 4 MiB）
az webapp config appsettings set -g rg-paster -n paster-sy4ce \
  --settings PASTER_MAX_FILE_BYTES=4194304

# 看应用日志
az webapp log download -g rg-paster -n paster-sy4ce --log-file logs.zip
```

改完设置实例会重启；放在 `/home/data/paster` 的文件不受重启和重新部署影响（只跟有效期有关）。

## 目录

```
Program.cs          端点、请求限制、转发头、404/410 页面
TempStore.cs        磁盘存取、配额、到期删除
PasterOptions.cs    PASTER_* 环境变量
ExpirySweeper.cs    后台清理任务
wwwroot/            单页前端（无外部依赖，不加载任何 CDN 资源）
tools/smoke.py      冒烟测试：上传、下载字节比对、下载响应头、超限、404
tools/verify-expiry.py  真等到期再验一次，确认链接确实作废
```

`verify-expiry.py` 每周一自动跑一次（`verify-expiry` workflow，不需要 Azure 凭据），
因为「五分钟后失效」这句话只有等够五分钟才能验证。
