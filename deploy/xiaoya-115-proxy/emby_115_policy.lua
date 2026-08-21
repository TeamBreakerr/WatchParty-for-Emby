local M = {}

local STATE_DICT = "emby_115_classification"
local NON_115_TTL_SECONDS = 60

local allowed_suffixes = {
    "115cdn.net",
    "115cdn.com",
    "115.com",
}

local function has_dns_suffix(host, suffix)
    return host == suffix
        or (#host > #suffix and host:sub(-#suffix - 1) == "." .. suffix)
end

function M.get_115_target_host(target)
    if type(target) ~= "string" or target == "" then
        return nil
    end

    local scheme, authority = target:match("^([%a][%w+.-]*)://([^/%?#]+)")
    if not scheme or not authority or scheme:lower() ~= "https" then
        return nil
    end

    if authority:find("@", 1, true) or authority:find("[", 1, true) then
        return nil
    end

    local host = authority:match("^([^:]+):%d+$") or authority
    host = host:lower():gsub("%.$", "")
    if host == "" or host:find(":", 1, true) then
        return nil
    end

    for _, suffix in ipairs(allowed_suffixes) do
        if has_dns_suffix(host, suffix) then
            return host
        end
    end

    return nil
end

local function state()
    return ngx.shared[STATE_DICT]
end

local function path_hash(uri)
    return ngx.md5(uri or "")
end

function M.should_probe(uri)
    local classification = state()
    return not classification
        or classification:get("classification:" .. path_hash(uri)) ~= "non115"
end

function M.mark_non_115(uri)
    local classification = state()
    if classification then
        classification:set(
            "classification:" .. path_hash(uri),
            "non115",
            NON_115_TTL_SECONDS)
    end
end

function M.resolve_115_target(link_uri, args)
    local response = ngx.location.capture(link_uri, {
        args = args,
        method = ngx.HTTP_GET,
    })

    if response.status ~= ngx.HTTP_MOVED_PERMANENTLY
        and response.status ~= ngx.HTTP_MOVED_TEMPORARILY
        and response.status ~= ngx.HTTP_TEMPORARY_REDIRECT
        and response.status ~= 308 then
        if response.status == ngx.HTTP_OK
            or response.status == ngx.HTTP_PARTIAL_CONTENT then
            return nil, nil, "direct"
        end
        return nil, nil, "resolver_error"
    end

    local target = response.header["Location"] or response.header["location"]
    local host = M.get_115_target_host(target)
    if not host then
        return nil, nil, "non115"
    end

    return target, host, nil
end

return M
