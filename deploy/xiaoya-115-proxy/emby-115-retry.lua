local policy = require("emby_115_policy")

if ngx.var.emby_115_attempt ~= "0" then
    return ngx.exit(ngx.HTTP_FORBIDDEN)
end

ngx.var.emby_115_attempt = "1"
ngx.sleep(0.35)

local target, host = policy.resolve_115_target(
    ngx.var.emby_115_link_uri,
    ngx.var.emby_115_link_args)
if not target then
    ngx.log(ngx.ERR, "could not refresh a 115 download link during the bounded retry")
    return ngx.exit(ngx.HTTP_BAD_GATEWAY)
end

ngx.var.emby_115_target = target
ngx.var.emby_115_target_host = host
