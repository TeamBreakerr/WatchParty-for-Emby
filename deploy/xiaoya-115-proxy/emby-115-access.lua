local policy = require("emby_115_policy")

if ngx.req.get_method() ~= "GET" then
    return
end

local original_uri = ngx.var.uri
if not policy.should_probe(original_uri) then
    return
end

local link_uri = original_uri:gsub("^/d/", "/__emby_115_resolve/", 1)
local target, host, reason = policy.resolve_115_target(link_uri, ngx.var.args)
if not target then
    if reason == "non115" or reason == "direct" then
        policy.mark_non_115(original_uri)
    end
    return
end

ngx.var.emby_115_target = target
ngx.var.emby_115_target_host = host
ngx.var.emby_115_link_uri = link_uri
ngx.var.emby_115_link_args = ngx.var.args or ""
ngx.var.emby_115_key = ngx.md5(original_uri)

return ngx.exec("@emby_115_stream")
