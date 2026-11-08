-- DCS VoiceCom test hook: finds out which mission, map and server info a hook can read.
-- Install: copy to  Saved Games\DCS\Scripts\Hooks\DcsVoiceComProbe.lua
-- Output:          Saved Games\DCS\Logs\dcs.log (lines tagged VOICECOM.PROBE)
--                  and Saved Games\DCS\Logs\dcs-voicecom-probe.log if the hook may write files.
-- Read-only: it only writes log lines. Remove it when the test is done.

-- Every line goes to dcs.log (always works in a hook) and, if file access works, to its own file too.
local logPath = nil
pcall(function() logPath = lfs.writedir() .. "Logs\\dcs-voicecom-probe.log" end)
local fileFailed = false

local function write(line)
    pcall(log.write, "VOICECOM.PROBE", log.INFO, line)
    if fileFailed or not logPath then return end
    local ok, f, err = pcall(io.open, logPath, "a")
    if ok and f then
        pcall(function() f:write(os.date("%Y-%m-%d %H:%M:%S"), "  ", line, "\n") end)
        f:close()
    else
        fileFailed = true
        pcall(log.write, "VOICECOM.PROBE", log.INFO, "cannot write own log file: " .. tostring(err or f))
    end
end

local show
show = function(v, depth)
    depth = depth or 0
    if type(v) ~= "table" then
        local text = tostring(v):gsub("[\r\n]+", " | ")
        if #text > 400 then text = text:sub(1, 400) .. "..." end
        return text
    end
    if depth >= 2 then return "{...}" end
    local parts, n = {}, 0
    for k, val in pairs(v) do
        n = n + 1
        if n > 30 then parts[#parts + 1] = "..."; break end
        local shown = (k == "ucid" or k == "ipaddr") and "<hidden>" or show(val, depth + 1)
        parts[#parts + 1] = tostring(k) .. "=" .. shown
    end
    return "{" .. table.concat(parts, ", ") .. "}"
end

-- Calls a function if it exists and logs the result or the error.
local function try(label, tbl, name, ...)
    local fn = tbl and tbl[name]
    if type(fn) ~= "function" then
        write(label .. ": (not available)")
        return nil
    end
    local ok, result = pcall(fn, ...)
    if ok then
        write(label .. " = " .. show(result))
        return result
    end
    write(label .. ": ERROR " .. tostring(result))
    return nil
end

-- Lists every function name in a table, to spot calls we don't know about yet.
local function listFunctions(name, tbl)
    if type(tbl) ~= "table" then
        write(name .. ": (no table)")
        return
    end
    local names = {}
    for k, v in pairs(tbl) do
        if type(v) == "function" then names[#names + 1] = tostring(k) end
    end
    table.sort(names)
    write(name .. " functions: " .. table.concat(names, ", "))
end

-- Briefing texts in a .miz are often keys like "DictKey_descriptionText_1"; the text sits in a dictionary.
local function lookup(current, value)
    if type(value) ~= "string" or not value:find("^DictKey_") then return value end
    for _, dict in ipairs({ current.dictionary, current.mission and current.mission.dictionary }) do
        if type(dict) == "table" and dict[value] then return dict[value] end
        if type(dict) == "table" and type(dict.DEFAULT) == "table" and dict.DEFAULT[value] then return dict.DEFAULT[value] end
    end
    return value .. " (not found in dictionary)"
end

local function briefing(current, label, value)
    local text = tostring(lookup(current, value))
    if #text > 600 then text = text:sub(1, 600) .. "..." end
    write("briefing " .. label .. " = " .. (text:gsub("[\r\n]+", " | ")))
end

-- Looks for our server in the server browser list by its address.
function findInServerList(host)
    local ok, list = pcall(function() return net.serverlist_get() end)
    if not ok or type(list) ~= "table" then
        write("net.serverlist_get(): " .. tostring(list))
        return
    end
    local n, first = 0, nil
    for _, entry in pairs(list) do
        n = n + 1
        first = first or entry
        if host and type(entry) == "table" then
            local ip = tostring(host):match("^[^:]+")
            for _, v in pairs(entry) do
                if type(v) == "string" and ip and v:find(ip, 1, true) then
                    write("server list match = " .. show(entry))
                    break
                end
            end
        end
    end
    write("net.serverlist_get(): " .. n .. " entries, first = " .. show(first))
end

-- DCS logs "ASYNCNET ... Server name: <name>" when we join. See if a hook can read that back.
function findInLogHistory()
    local ok, history = pcall(function() return DCS.getLogHistory() end)
    if not ok or history == nil then
        write("DCS.getLogHistory(): " .. tostring(history))
        return
    end
    write("DCS.getLogHistory(): type " .. type(history))
    local function scan(text)
        if type(text) == "table" then
            for _, v in pairs(text) do scan(v) end
        elseif type(text) == "string" then
            for name in text:gmatch("Server name:%s*([^\r\n]+)") do write("log history server name = " .. name) end
        end
    end
    scan(history)
    if type(history) == "table" then
        local n = 0
        for _ in pairs(history) do n = n + 1 end
        write("DCS.getLogHistory(): " .. n .. " entries, sample = " .. show(history[1] or history[n]))
    end
end

local function probe(event)
    write("==== " .. event .. " ====")
    try("DCS.isMultiplayer()", DCS, "isMultiplayer")
    try("DCS.isServer()", DCS, "isServer")
    try("DCS.getMissionName()", DCS, "getMissionName")
    try("DCS.getMissionFilename()", DCS, "getMissionFilename")
    try("DCS.getPlayerUnitType()", DCS, "getPlayerUnitType")

    try("DCS.getMissionTheatre()", DCS, "getMissionTheatre")
    try("DCS.getTheatreID()", DCS, "getTheatreID")
    try("DCS.getMissionDescription()", DCS, "getMissionDescription")
    try("DCS.getPlayerBriefing()", DCS, "getPlayerBriefing")
    try("DCS.getPlayerCoalition()", DCS, "getPlayerCoalition")

    local okMission, current = pcall(DCS.getCurrentMission)
    if okMission and type(current) == "table" then
        local m = current.mission
        if type(m) == "table" then
            write("mission.theatre = " .. tostring(m.theatre))
            write("mission.sortie = " .. tostring(m.sortie))
            write("mission.date = " .. show(m.date))
            briefing(current, "descriptionText", m.descriptionText)
            briefing(current, "descriptionBlueTask", m.descriptionBlueTask)
            briefing(current, "descriptionRedTask", m.descriptionRedTask)
            briefing(current, "descriptionNeutralsTask", m.descriptionNeutralsTask)
            local top = {}
            for k in pairs(current) do top[#top + 1] = tostring(k) end
            table.sort(top)
            write("getCurrentMission() keys: " .. table.concat(top, ", "))
            local keys = {}
            for k in pairs(m) do keys[#keys + 1] = tostring(k) end
            table.sort(keys)
            write("mission keys: " .. table.concat(keys, ", "))
        else
            write("DCS.getCurrentMission(): no .mission table, keys: " .. show(current))
        end
    else
        write("DCS.getCurrentMission(): ERROR or empty " .. tostring(current))
    end

    local myId = try("net.get_my_player_id()", net, "get_my_player_id")
    try("net.get_server_id()", net, "get_server_id")
    if myId then try("net.get_player_info(me)", net, "get_player_info", myId) end
    try("net.get_player_list()", net, "get_player_list")
    local host = try("net.get_server_host()", net, "get_server_host")
    try("net.get_server_settings()", net, "get_server_settings")
    try("net.serverinfo_get()", net, "serverinfo_get")
    try("net.serverinfo_request()", net, "serverinfo_request")
    findInServerList(host)
    findInLogHistory()
    try("net.get_name(me)", net, "get_name", myId or 0)
end

local handler = {}

function handler.onMissionLoadEnd() probe("onMissionLoadEnd") end
function handler.onSimulationStart() probe("onSimulationStart") end
function handler.onNetConnect(localPlayerId) probe("onNetConnect id=" .. tostring(localPlayerId)) end
function handler.onNetMissionChanged(name) probe("onNetMissionChanged name=" .. tostring(name)) end
function handler.onPlayerChangeSlot(id)
    local okMe, me = pcall(function() return net.get_my_player_id() end)
    if okMe and id ~= me then return end
    probe("onPlayerChangeSlot id=" .. tostring(id))
end
function handler.onSimulationStop() write("==== onSimulationStop ====") end
function handler.onNetDisconnect(reason) write("==== onNetDisconnect " .. tostring(reason) .. " ====") end

local ok, err = pcall(function()
    write("==== probe loaded ====")
    listFunctions("DCS", DCS)
    listFunctions("net", net)
    DCS.setUserCallbacks(handler)
end)
if not ok then
    pcall(log.write, "VOICECOM.PROBE", log.ERROR, tostring(err))
end
