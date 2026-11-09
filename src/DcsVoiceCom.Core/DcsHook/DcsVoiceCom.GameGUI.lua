-- DCS VoiceCom hook: tells DCS VoiceCom which multiplayer server and mission you're on, so it can turn on
-- that server's profile. Installed by DCS VoiceCom (Settings > DCS hook) into Saved Games\DCS\Scripts\Hooks.
-- It only reads the server name and address, mission name and map, and sends them to this PC (127.0.0.1).
-- DCS VoiceCom hook version 1

local PORT = {{PORT}}
local REPEAT_SECONDS = 10

local ok, socket = pcall(function()
    package.path = package.path .. ";" .. lfs.currentdir() .. "/LuaSocket/?.lua"
    package.cpath = package.cpath .. ";" .. lfs.currentdir() .. "/LuaSocket/?.dll"
    return require("socket")
end)
if not ok or not socket then
    pcall(log.write, "DCSVOICECOM", log.ERROR, "LuaSocket not available: " .. tostring(socket))
    return
end

local udp = socket.udp()
local running = false
local lastSent = 0
local message = nil -- the last mission message, repeated while the mission runs

local function call(fn, ...)
    if type(fn) ~= "function" then return nil end
    local good, result = pcall(fn, ...)
    if good then return result end
    return nil
end

local function trim(text)
    if type(text) ~= "string" then return "" end
    return (text:gsub("^%s+", ""):gsub("%s+$", ""))
end

local function quote(text)
    text = tostring(text or "")
    text = text:gsub('[%c"\\]', function(c)
        if c == '"' then return '\\"' end
        if c == "\\" then return "\\\\" end
        return string.format("\\u%04x", c:byte())
    end)
    return '"' .. text .. '"'
end

local function send(text)
    pcall(function() udp:sendto(text, "127.0.0.1", PORT) end)
end

-- Only calls tested safe on a multiplayer client. Never DCS.getTheatreID(): it crashes DCS before the map loads.
-- net.get_server_settings() also holds a password hash: only its name is read.
local function build()
    local mp = call(DCS.isMultiplayer) == true
    local server, address = "", ""
    if mp then
        local settings = call(net.get_server_settings)
        if type(settings) == "table" then server = trim(settings.name) end
        address = trim(call(net.get_server_host))
    end
    local map = call(DCS.getMissionTheatre)
    if type(map) ~= "string" or map == "" then
        local current = call(DCS.getCurrentMission)
        map = type(current) == "table" and type(current.mission) == "table" and current.mission.theatre or ""
    end
    return '{"v":1,"event":"mission","mp":' .. tostring(mp) ..
        ',"server":' .. quote(server) .. ',"address":' .. quote(address) ..
        ',"mission":' .. quote(trim(call(DCS.getMissionName))) .. ',"map":' .. quote(trim(map)) .. '}'
end

local function now()
    return call(DCS.getRealTime) or os.clock()
end

local function report()
    message = build()
    send(message)
    lastSent = now()
end

local function stop()
    if not running then return end
    running = false
    message = nil
    send('{"v":1,"event":"stop"}')
end

local handler = {}

function handler.onSimulationStart()
    running = true
    report()
end

function handler.onPlayerChangeSlot(id)
    if running and id == call(net.get_my_player_id) then report() end
end

function handler.onSimulationFrame()
    if running and message and now() - lastSent >= REPEAT_SECONDS then
        send(message)
        lastSent = now()
    end
end

handler.onSimulationStop = stop
handler.onNetDisconnect = stop

local registered, err = pcall(DCS.setUserCallbacks, handler)
if not registered then pcall(log.write, "DCSVOICECOM", log.ERROR, tostring(err)) end
