# RelaxKon Docker Compose 部署指南

适用于单机全新部署。ASP.NET Core 容器同时提供 Angular 网页和同源 API。需要 Docker Engine、Docker Compose、Git，以及构建前端用的 Node.js 24 和 npm。以下命令使用 Bash。

## 1. 构建前端

在同一个父目录克隆前后端仓库，然后从前端的生产构建目录复制浏览器产物。请使用相互兼容的前后端版本。

```sh
git clone https://github.com/nanaminato/relaxkon-server.git
git clone https://github.com/nanaminato/relaxkon-web.git
cd relaxkon-web
npm ci
npm run build -- --configuration production
test -f dist/app/browser/index.html
mkdir -p ../relaxkon-server/src/RelaxKon.Auth/wwwroot
cp -R dist/app/browser/. ../relaxkon-server/src/RelaxKon.Auth/wwwroot/
cd ../relaxkon-server
```

只部署 API 时，可以直接进入后端仓库，跳过前端构建。

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
docker compose build server
docker compose run --rm server --init-db
docker compose up -d
docker compose ps
docker compose logs --tail=100 server
```

`--init-db` 只用于空库，会交互式要求填写首个管理员的用户名、邮箱和密码。管理员密码须为 6–20 个字符，密码提示处直接回车可自动生成。已有兼容数据库可以直接启动。修改 `.env` 后运行 `docker compose up -d`，重新创建容器以加载配置。

容器显示为运行中时，应用可能还在检查数据库。请等启动日志出现监听地址，再访问页面或登录接口。

## 4. 配置 HTTPS 并验收

后端默认绑定到宿主机的 `127.0.0.1:6000`。生产部署时，让宿主机反向代理为前端域名终止 TLS，并将该域名的请求转发到 `http://127.0.0.1:6000`。代理应关闭流式接口的响应缓冲，保留应用发送数据的时机。如需识别真实客户端 IP，按实际代理地址配置 `ForwardedHeaders__KnownProxies__0`，并让代理传递转发头。

1. 在宿主机执行 `curl -fsS http://127.0.0.1:6000/ -o /dev/null` 检查一体部署的首页。随后通过公开 HTTPS 域名打开页面，刷新一个应用内的深层路由。
2. 使用刚创建的管理员账户登录，添加供应商密钥、模型和关联，发送一条聊天消息并确认回复逐步显示。配置接口见 [API 使用](api.md)。
3. 如启用了 SMTP，验证密码重置邮件能送达，链接指向实际 HTTPS 域名。若同时开放了注册，再验证邮箱验证码。

常规重建和重启会保留命名卷。备份时应包含 SQLite 数据库和 Data Protection 密钥。使用外部 MySQL 时也需要单独备份 MySQL 数据。`docker compose down -v` 会删除 Compose 命名卷。

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
