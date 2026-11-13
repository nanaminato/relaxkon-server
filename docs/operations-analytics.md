# 运营概览

模型用量统计上游实际返回的输入和输出 Token。未报告用量的请求仍计入请求量、错误和耗时。OpenAI 图像按响应中的成功结果计数。APIMart 按首次查询到的已完成任务统计，重复轮询通过任务 ID 的哈希去重，未查询的结果不会计入。成功率按 HTTP 请求计算，流式聊天开始输出后的上游失败也会计为错误。

活跃用户按成功使用功能的账号去重。新用户趋势从启用后成功完成的邮箱注册开始，按 UTC 注册日期分组。次日和 7 日留存分别按注册后第 1 和第 7 个 UTC 日统计，未结束的观察日显示为空。注册趋势是全站统计。

分析存储沿用原 SQLite 文件并自动加列，保留 37 天数据，用于计算 30 天报表和留存。分析字段包括路由模板、模型标识、状态、耗时、体积区间、数字账号 ID 和用量计数。

## 接入 Nginx 的同步 413

在 Nginx 的 `http` 上下文中配置以下内容：

```nginx
map "$uri|$status|$upstream_status" $relaxkon_sync_rejected {
    default 0;
    "/api/sync/data|413|" 1;
    "/api/sync/data|413|-" 1;
}
log_format relaxkon_sync_size '$msec $request_method $request_time $http_content_length';
```

在 RelaxKon 的 HTTPS `server` 块添加以下专用日志，保留原有的访问日志配置：

```nginx
access_log /var/log/relaxkon-analytics/sync-rejected.log relaxkon_sync_size if=$relaxkon_sync_rejected;
```

创建日志目录，赋予 Nginx 写入权限以及应用容器用户读取权限。将日志目录只读挂载到应用容器，例如 `/var/log/relaxkon-analytics:/edge-analytics:ro`，再配置 `Analytics__EdgeLogPath=/edge-analytics/sync-rejected.log`。保留最近 37 天的未压缩数字后缀轮转文件，并让 Nginx 在轮转后重新打开日志。运行 `nginx -t` 后再重载。专用日志只包含时间、方法、耗时和用于计算体积区间的 Content-Length。
