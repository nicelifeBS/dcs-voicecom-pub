-- DCS VoiceCom test hook: finds out which mission, map and server info a hook can read.
-- Install: copy to  Saved Games\DCS\Scripts\Hooks\DcsVoiceComProbe.lua
-- Output:          Saved Games\DCS\Logs\dcs-voicecom-probe.log
-- Read-only: it only writes the log file. Remove it when the test is done.

local logPath = lfs.writedir() .. "Logs\\dcs-voicecom-probe.log"

local function write(line)
    local f = io.open(logPath, "a")
    if f then
        f:write(os.date("%Y-%m-%d %H:%M:%S"), "  ", line, "\n")
        f:close()
    end
end

local function show(v, depth)
    depth = depth or 0
    if type(v) ~= "table" then return tostring(v) end
    if depth >= 2 then return "{...}" end
    local parts, n = {}, 0
    for k, val in pairs(v) do
        n = n + 1
        if n > 30 then parts[#parts + 1] = "..."; break end
        parts[#parts + 1] = tostring(k) .. "=" .. show(val, depth + 1)
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

local function probe(event)
    write("==== " .. event .. " ====")
    try("DCS.isMultiplayer()", DCS, "isMultiplayer")
    try("DCS.isServer()", DCS, "isServer")
    try("DCS.getMissionName()", DCS, "getMissionName")
    try("DCS.getMissionFilename()", DCS, "getMissionFilename")
    try("DCS.getPlayerUnitType()", DCS, "getPlayerUnitType")

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
    try("net.get_server_host()", net, "get_server_host")
    try("net.get_name(me)", net, "get_name", myId or 0)
end

local handler = {}

function handler.onMissionLoadEnd() probe("onMissionLoadEnd") end
function handler.onSimulationStart() probe("onSimulationStart") end
function handler.onNetConnect(localPlayerId) probe("onNetConnect id=" .. tostring(localPlayerId)) end
function handler.onNetMissionChanged(name) probe("onNetMissionChanged name=" .. tostring(name)) end
function handler.onPlayerChangeSlot(id) probe("onPlayerChangeSlot id=" .. tostring(id)) end
function handler.onSimulationStop() write("==== onSimulationStop ====") end
function handler.onNetDisconnect(reason) write("==== onNetDisconnect " .. tostring(reason) .. " ====") end

local ok, err = pcall(function()
    write("==== probe loaded ====")
    listFunctions("DCS", DCS)
    listFunctions("net", net)
    DCS.setUserCallbacks(handler)
end)
if not ok then
    pcall(log.write, "DcsVoiceComProbe", log.ERROR, tostring(err))
end
