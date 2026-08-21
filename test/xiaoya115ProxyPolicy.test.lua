local repo_root = assert(arg[1], "repository root is required")
package.path = repo_root .. "/deploy/xiaoya-115-proxy/?.lua;" .. package.path

local policy = require("emby_115_policy")

local function assert_equal(expected, actual, message)
    if expected ~= actual then
        error((message or "values differ") .. ": expected " .. tostring(expected)
            .. ", got " .. tostring(actual), 2)
    end
end

local valid_targets = {
    "https://cdnfhnfile.115cdn.net/path/video.mkv?token=redacted",
    "https://115cdn.net/video.mkv",
    "https://edge.115cdn.com/video.mkv",
    "https://download.115.com/video.mkv",
}

for _, target in ipairs(valid_targets) do
    local host = policy.get_115_target_host(target)
    assert(host, "expected a 115 target: " .. target)
end

local invalid_targets = {
    "https://not115cdn.net/video.mkv",
    "https://115cdn.net.example.com/video.mkv",
    "https://example.com/115cdn.net/video.mkv",
    "ftp://cdnfhnfile.115cdn.net/video.mkv",
    "javascript:alert(1)",
    "",
}

for _, target in ipairs(invalid_targets) do
    assert_equal(nil, policy.get_115_target_host(target),
        "unexpected 115 target match for " .. target)
end

assert_equal("cdnfhnfile.115cdn.net",
    policy.get_115_target_host("HTTPS://CDNFHNFILE.115CDN.NET:443/video.mkv"))
assert_equal(nil, policy.get_115_target_host("https://127.0.0.1/video.mkv"))

print("xiaoya 115 policy tests passed")
