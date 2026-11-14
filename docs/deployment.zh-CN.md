# RelaxKon Docker Compose 部署指南

适用于 Linux x86-64 单机部署。GHCR 镜像同时提供 Angular 网页和同源 API。部署机需要 Docker Engine、Docker Compose 和 Git。以下命令使用 Bash。

## 1. 获取部署配置

```sh
git clone https://github.com/nanaminato/relaxkon-server.git
cd relaxkon-server
```

Compose 默认使用 `ghcr.io/nanaminato/relaxkon-server:latest`。可在 `.env` 中设置 `RELAXKON_IMAGE`，指定发布标签或 `ghcr.io/nanaminato/relaxkon-server@sha256:…` 来固定版本。

GHCR 公共镜像支持匿名拉取。若镜像包处于私有状态，先使用对该包有读取权限的 GitHub 账号和带 `read:packages` 权限的 classic PAT 登录，再执行下方的拉取命令。

```sh
read -rsp 'GHCR token: ' ghcr_token
printf '\n'
printf '%s' "$ghcr_token" | docker login ghcr.io -u YOUR_GITHUB_USERNAME --password-stdin
unset ghcr_token
```

## 2. 配置数据库、JWT 和邮箱

复制配置模板，再编辑 `.env`。

```sh
cp .env.example .env
```

- **JWT** 填写 `Jwt__SecretKey` 和 `Jwt__RefreshTokenPepper`。分别运行两次 `openssl rand -hex 32` 可生成两个独立的随机值。新实例使用模板中的 issuer 和 audience。若沿用已有部署，保留其实际 JWT 标识、密钥及 pepper。
- **SQLite** 默认启用。数据库文件和 Data Protection 密钥保存在 Compose 命名卷中，适用于单实例。
- **MySQL** 如需使用外部 MySQL 8.4，请先创建空数据库和应用账号，并给初始化账号建表、索引和触发器所需权限。使用容器可访问的主机名，配置见下方示例。
- **SMTP** 默认关闭。若要启用密码重置，设置 `PasswordReset__Enabled=true`，填写公开的前端 HTTPS 地址、SMTP 主机、端口、账号、发件地址和授权密码。若要开放邮箱验证注册，再设置 `Registration__Enabled=true`。端口 465 使用隐式 TLS，587 使用 STARTTLS，均保留 `PasswordReset__EnableSsl=true`。
- **Redis** 可选。需要时填写 `ConnectionStrings__Redis`。
- **CORS** 前后端同源时可留空。单独托管前端时，仍应将前端站点上的 API 请求转发给后端，以配合当前前端的同源 API 地址。

MySQL 配置示例：

```dotenv
Database__Provider=MySql
ConnectionStrings__ciko='Server=db.example.com;Port=3306;Database=relaxkon;User ID=relaxkon;Password=replace-me;Charset=utf8mb4'
```

含 `$` 的连接串或 SMTP 密码用单引号包裹，避免 Compose 展开变量。`.env` 含有部署凭据，应限制文件读取权限。

## 3. 初始化并启动

```sh
docker compose pull server
docker compose run --rm server --init-db
docker compose up -d
docker compose ps
docker compose logs --tail=100 server
```

`--init-db` 只用于空库，会交互式要求填写首个管理员的用户名、邮箱和密码。管理员密码须为 6–20 个字符，密码提示处直接回车可自动生成。已有兼容数据库可以直接启动。修改 `.env` 后运行 `docker compose up -d`，重新创建容器以加载配置。

容器显示为运行中时，应用可能还在检查数据库。请等启动日志出现监听地址，再访问页面或登录接口。

## 4. 配置 HTTPS 并验收

后端默认绑定到宿主机的 `127.0.0.1:6000`。Chromium 等浏览器会拦截直接访问端口 `6000`。如需在宿主机用浏览器直连验收，可在 `.env` 中设置 `RELAXKON_PORT=6080`，执行 `docker compose up -d` 后访问 `http://127.0.0.1:6080`。生产部署时，让宿主机反向代理为前端域名终止 TLS，并将该域名的请求转发到宿主机上的 Compose 监听地址（默认 `http://127.0.0.1:6000`）。代理应关闭流式接口的响应缓冲，保留应用发送数据的时机。如需识别真实客户端 IP，按实际代理地址配置 `ForwardedHeaders__KnownProxies__0`，并让代理传递转发头。

1. 在宿主机执行 `curl -fsS http://127.0.0.1:6000/ -o /dev/null` 检查一体部署的首页。若已更改宿主机端口，请替换命令中的端口。随后通过公开 HTTPS 域名打开页面，刷新一个应用内的深层路由。
2. 在浏览器打开 `/accounts/sign-in`，输入刚创建的管理员用户名、密码和页面显示的验证码。登录后打开 `/admin/accounts/account-management`，确认列表中显示该管理员。随后添加供应商密钥、模型和关联，发送一条聊天消息并确认回复逐步显示。配置接口见 [API 使用](api.md)。
3. 如启用了 SMTP，验证密码重置邮件能送达，链接指向实际 HTTPS 域名。若同时开放了注册，再验证邮箱验证码。

仓库的 [部署验收工作流](../.github/workflows/deployment-smoke.yml) 使用固定的前端提交构建一体镜像。镜像通过空库和 Chromium 登录验收后推送至 GHCR，再按摘要拉取到另一个全新 SQLite 卷复验。复验通过后更新 `latest`。

常规重建和重启会保留命名卷。备份时应包含 SQLite 数据库和 Data Protection 密钥。使用外部 MySQL 时也需要单独备份 MySQL 数据。`docker compose down -v` 会删除 Compose 命名卷。

## Nginx 同步接口的请求体上限

在生产站点的 HTTPS `server` 块中，为 `/api/sync/data` 配置 **28 MiB** 的请求体上限。聊天附件以 Base64 随消息同步，请求体会大于原始文件。生产环境曾因 Nginx 默认的 1 MiB 上限拒绝一笔 6,731,609 字节的同步写入。浏览器会先提交待同步记录，这笔写入返回 413 后，会话历史也无法继续拉取。

以现有 `location /api/` 为模板添加以下精确匹配规则，沿用该部署的转发头、缓冲和超时设置。示例使用生产 Compose 的宿主机监听端口 `6080`，部署时按实际端口填写。

```nginx
location = /api/sync/data {
    client_max_body_size 28m;
    include proxy_params;
    proxy_pass http://127.0.0.1:6080;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_request_buffering off;
    proxy_buffering off;
    proxy_read_timeout 300s;
}
```

备份站点配置，执行 `sudo nginx -t`，通过后执行 `sudo systemctl reload nginx`。

通过公开 HTTPS 域名向 `POST /api/sync/data` 发送两份只含 `padding` 字段的 JSON。使用已登录账户的 Bearer 令牌和匹配的 `X-Sync-Account` 请求头。这两份请求缺少同步操作的必填字段，不会写入数据。

| `padding` 字符数（ASCII） | 预期响应 |
| --- | --- |
| 6,731,609 | 应用返回 HTTP 400 和 JSON 校验错误，表明请求已通过 Nginx |
| 30,000,000 | Nginx 返回 HTTP 413，表明 28 MiB 上限仍生效 |

最后在浏览器刷新聊天页。有待同步记录时点击「重试同步」，确认会话重新出现在列表中。打开含生成附件的会话，点击附件卡片上的「下载」，核对实际下载文件的内容。若仍遇到 413，在 Nginx 错误日志中查找 `client intended to send too large body`，核对请求路径和字节数。有关拒绝记录的统计配置，参见 [运营概览](operations-analytics.md#接入-nginx-的同步-413)。

## 从源码构建镜像

如需构建自定义镜像，在后端仓库旁克隆 `relaxkon-web`，选择兼容的前端版本，然后用 Node.js 24 和 npm 构建。以下命令在后端仓库根目录执行。

```sh
npm ci --prefix ../relaxkon-web
npm run build --prefix ../relaxkon-web -- --configuration production
test -f ../relaxkon-web/dist/app/browser/index.html
mkdir -p src/RelaxKon.Auth/wwwroot
cp -R ../relaxkon-web/dist/app/browser/. src/RelaxKon.Auth/wwwroot/
docker build -t relaxkon-local:custom .
```

在 `.env` 中设置 `RELAXKON_IMAGE=relaxkon-local:custom`，跳过拉取步骤，再执行初始化和启动命令。

## 非容器部署（终端初始化，再由 systemd 启动）

先按 [构建与运行](deployment.md#构建与运行) 发布后端。下面假设发布产物位于 `/opt/relaxkon`，服务账号为 `relaxkon`，且已安装 .NET 10 ASP.NET Core 运行时。将数据库连接串、JWT 和按需启用的 SMTP 配置放入 `/etc/relaxkon/server.env`。每行使用 `KEY=value` 格式，无需写 `export`。不使用 Redis 时显式设置 `ConnectionStrings__Redis=`，以覆盖发布目录中可能存在的默认连接串。可用 `ASPNETCORE_URLS=http://127.0.0.1:6000` 设置本机反向代理的监听地址。含空格或 `$` 的值用单引号包裹。限制该文件的读取权限，并允许服务账号读取它。

使用 SQLite 时，设置 `Database__Provider=Sqlite`，连接串使用绝对路径，例如 `ConnectionStrings__ciko='Data Source=/var/lib/relaxkon/relaxkon.db'`。确保服务账号可以写入数据库目录和 `DataProtection__KeyPath` 指定的密钥目录。

1. 在终端用服务账号执行一次空库初始化，按提示创建首个管理员。密码须为 6–20 个字符，也可以直接回车自动生成。成功后命令会自行退出。

   ```sh
   sudo -u relaxkon bash -c 'cd /opt/relaxkon && set -a && . /etc/relaxkon/server.env && exec /usr/bin/dotnet RelaxKon.Auth.dll --init-db'
   ```

2. 创建 `/etc/systemd/system/relaxkon.service`，让服务使用同一份数据库配置。正式启动命令省略 `--init-db`。

   ```ini
   [Unit]
   Description=RelaxKon Server
   After=network.target

   [Service]
   User=relaxkon
   WorkingDirectory=/opt/relaxkon
   Environment=ASPNETCORE_ENVIRONMENT=Production
   EnvironmentFile=/etc/relaxkon/server.env
   ExecStart=/usr/bin/dotnet /opt/relaxkon/RelaxKon.Auth.dll
   Restart=on-failure

   [Install]
   WantedBy=multi-user.target
   ```

3. 启用服务并查看启动日志。使用其他进程守护工具时，同样指定服务账号、工作目录、环境配置和正式启动命令。

   ```sh
   sudo systemctl daemon-reload
   sudo systemctl enable --now relaxkon.service
   sudo journalctl -u relaxkon.service -n 100 --no-pager
   ```

详细配置见 [部署与配置](deployment.md)。
