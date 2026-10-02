--[[
main.lua —— 用 OmniSubs 给正在播的这一部片子出中文字幕。

识别在另一个进程里做，播放不受影响：插件起 omnisubs.exe，盯着它写出来的那份 `.srt` ——
程序每走完一个窗口就把这份字幕整份重写一遍（见 README「边翻译边用」），所以第一个窗口之后
就有能看的字幕，插件跟着把它挂上、跟着它更新，跑完再切过去。屏幕上一行常驻的字都不留。

三处是被 mpv 的机制定的，不是偏好：

  * 菜单项由插件自己插进 menu-data。右键菜单渲染的就是这个属性（内置的 context_menu.lua 里
    就是 add_menu(mp.get_property_native("menu-data"), x, y)），而它可写 —— mpv 手册原话：
    "Writing to this property ... will trigger an immediate update of the menu"。于是不必去动
    ~~/menu.conf：那份文件只是内置的 select.lua 读进来解析成 menu-data 的一份输入，
    为一项菜单把整个内置菜单抄一份，是替换式的做法，还会连别人插在那里的条目一起盖掉。

  * 子进程走 command_native_async。同步形式会等进程退出，等于把播放器冻住一整次识别的时间。

  * 字幕怎么重读由 attach() 那边的三条规矩定（自己摘自己挂、途中 auto、跑完 select），
    每一条的理由都写在那个函数上面 —— 都是 mpv 自己的做法逼出来的，不是挑的。

进度文件是两半之间的接口：omnisubs 结束时写下结论。插件从不解析程序的
控制台文字 —— 那是散文，随时可以改；它只从结论里读成没成、失败的原因是什么。另一条通路是字幕
文件本身：插件只看它多大，过的是内容，不是措辞。
]]

local NAME = "OmniSubs"

-- 隔多久看一眼字幕文件，秒；它一变（又长了一段）就重读一遍 —— 轮询只做这一件事。
local POLL_INTERVAL = 1

-- 画面通道用哪个档位（omnisubs.json 里 models 的键），与音频同一个默认档。
local VIDEO_PROFILE = "default"

--[[
菜单里那几项。两种"翻译"就是命令行"有没有 --model-video"这同一个说法的两个入口：
只要音轨，或者再加上画面上的字。

条目本身是静态的：有没有正在跑的任务，由点下去之后那句提示回答。菜单里再维护一份状态不值当，
因为 menu-data 会被 select.lua 整份重写，写进去的状态很快就会和事实对不上。
]]
local MENU = {
    type = "submenu",
    title = NAME,
    submenu = {
        { title = "翻译音轨", cmd = "script-binding omnisubs/generate-audio" },
        { title = "翻译音轨和画面", cmd = "script-binding omnisubs/generate-video" },
        { title = "停止翻译", cmd = "script-binding omnisubs/cancel" },
    },
}

-- 正在跑的那一次；nil 就是没有。
local job = nil
local poll_timer = nil

-- ---------------------------------------------------------------- 小工具

local function file_exists(path)
    if not path or path == "" then
        return false
    end

    local handle = io.open(path, "rb")
    if not handle then
        return false
    end

    handle:close()
    return true
end

local function read_text(path)
    local handle = io.open(path, "rb")
    if not handle then
        return nil
    end

    local text = handle:read("*a")
    handle:close()
    return text
end

--- 文件现在有多少字节；读不到（还没有这个文件）就是 nil。
--- 拿它当"这份字幕变了没有"的判据：程序每走完一个窗口就把整份文件重写一遍，
--- 而它每次都是变长的，所以大小不变就是没变。
local function file_size(path)
    if not path then
        return nil
    end

    local handle = io.open(path, "rb")
    if not handle then
        return nil
    end

    local size = handle:seek("end")
    handle:close()
    return size
end

--- 识别进程要的是一个它自己能打开的名字。mpv 给的 path 一直是绝对路径（这一支因此没被走到过）；
--- 万一给的是相对名字，就按 mpv 的工作目录补成绝对的。
local function absolute(path)
    if path:match("^%a:[/\\]") or path:match("^[/\\]") then
        return path
    end

    local cwd = mp.get_property("working-directory")
    if cwd and cwd ~= "" then
        return cwd .. "/" .. path
    end

    return path
end

--- 字幕写在视频旁边、同名 .srt —— 与 omnisubs 自己的规矩一致。
local function srt_path(video)
    return (video:gsub("%.[^%.\\/]*$", "")) .. ".srt"
end

--- 找 omnisubs.exe：只找插件自己旁边这一处。
local function find_omnisubs()
    -- debug.getinfo 而不是 mp.get_script_directory —— 后者在某些 build 上返回空。
    local info = debug.getinfo(1, "S")
    local own = info and info.source and info.source:match("^@(.*)$")
    local beside = own and own:match("^(.*)[/\\][^/\\]*$")

    if beside and file_exists(beside .. "/omnisubs.exe") then
        return beside .. "/omnisubs.exe"
    end

    return nil
end

-- ---------------------------------------------------------------- 菜单

--[[
把自己那一项放进 menu-data；已经在里面就什么都不做。

要看着它、缺了就补，是因为 menu-data 不止我们在写：内置的 select.lua 在第一个画面窗口出现时
把 menu.conf（没有就用 default-menu）解析进去，之后刷新动态子菜单时还会整份重写一遍。
补回去这件事本身会再触发一次观察，但那一次已经看到条目在了，就此收敛。
]]
local function ensure_menu()
    local data = mp.get_property_native("menu-data")
    if type(data) ~= "table" then
        return
    end

    for _, item in ipairs(data) do
        if item.title == NAME then
            return
        end
    end

    table.insert(data, 1, MENU)
    mp.set_property_native("menu-data", data)
end

-- ---------------------------------------------------------------- 进度文件

--- 读进度文件：一行一条 key\tvalue。插件只取结论 —— exit（这次成没成）与失败时的 error。
local function read_progress(path)
    local text = read_text(path)
    if not text then
        return nil
    end

    local state = {}
    for line in text:gmatch("[^\r\n]+") do
        local key, value = line:match("^([^\t]*)\t?(.*)$")
        if key and key ~= "" then
            state[key] = value
        end
    end

    return state
end

-- ---------------------------------------------------------------- 任务

local function progress_path()
    return (os.getenv("TEMP") or ".") .. "/omnisubs-"
        .. tostring(mp.get_property("pid")) .. ".progress"
end

local function stop_polling()
    if poll_timer then
        poll_timer:kill()
        poll_timer = nil
    end
end

--- 把这一次任务的东西全放下。进度文件由我们收走，免得反复运行堆下一堆。
--- 画面上没有留下任何常驻的东西，所以这里没有要撤的。
local function release()
    stop_polling()

    if job then
        os.remove(job.progress)
        job = nil
    end
end

-- ---------------------------------------------------------------- 字幕挂载

--- 我们挂上去的那条轨的标题。挂的时候给的就是它，认也认它 ——
--- id 是挂上时才分配的，而且摘掉重挂必然换一个，认不住。
local function track_title(label)
    return NAME .. " · " .. label
end

local function find_track(label)
    local wanted = track_title(label)

    for _, track in ipairs(mp.get_property_native("track-list") or {}) do
        if track.type == "sub" and track.title == wanted then
            return track
        end
    end

    return nil
end

--- 除了我们这条，还有别的字幕轨正在显示吗。
local function others_showing(mine)
    for _, track in ipairs(mp.get_property_native("track-list") or {}) do
        if track.type == "sub" and track.selected and (not mine or track.id ~= mine.id) then
            return true
        end
    end

    return false
end

--- 把这份 .srt 挂上；已经挂着就摘掉重挂。
---
--- 重读只能这么做：mpv 没有"重读这个文件"，sub-reload 做的就是摘掉再挂 —— 但它**丢掉标题**
--- （源码里只搬 lang 与几个 flag），而标题正是下次认出这条轨的依据，丢了就会一次一次越挂越多；
--- 它还一律切过去，边跑边更新时会把用户正看的字幕抢走。自己摘自己挂，两样都由我们说了算。
---
--- want_select 为真（跑完了）才一定切过去。边跑边挂只在"当前压根没有字幕在显示"
--- （或者选着的本来就是我们这条）时才切，否则用 auto 静静挂着 —— 用户可能正看着内嵌的那条轨，
--- 途中每长一段就抢一次说不过去。
local function attach(path, label, want_select)
    local mine = find_track(label)
    local keep = want_select or (mine ~= nil and mine.selected == true) or not others_showing(mine)

    if mine then
        mp.commandv("sub-remove", tostring(mine.id))
    end

    mp.commandv("sub-add", path, keep and "select" or "auto", track_title(label))
end

--- 字幕文件一有新内容就挂上、重读一遍。程序每走完一个窗口就把这份文件整份重写（见 README
--- 「边翻译边用」），这里盯着它的大小 —— 每次都只有变长，所以大小变了就是又长了一段。
--- 这是那件事的另一半：程序那边把文件写早了，插件这边也得把它挂早，否则用户还是要等整次跑完。
local function follow()
    if not job then
        return
    end

    local size = file_size(job.srt)
    if size == nil or size == job.seen then
        return
    end

    local first = not job.attached
    job.attached = true
    job.seen = size

    attach(job.srt, job.label, false)

    if first then
        mp.osd_message(NAME .. " 字幕可以看了 · " .. job.label, 4)
    end
end

--- 收尾。进程结束的回调是唯一会走到这里的路；已经取消过的那一次，job 早清掉了，
--- 它的回调到这里自然变成空操作。
local function finish(success, result)
    if not job then
        return
    end

    local srt, label, attached = job.srt, job.label, job.attached
    local state = read_progress(job.progress)

    release()

    local status = type(result) == "table" and result.status or nil
    local failed = not success
        or (status ~= nil and status ~= 0)
        or (state and state.exit and state.exit ~= "0")

    if failed then
        -- 失败的原因只有程序自己说得准：终态记录里那句就是它报的。拿不到才退回退出码。
        local reason = state and state.error

        if not reason or reason == "" then
            reason = status and ("退出码 " .. tostring(status)) or "进程起不来"
        end

        mp.msg.error("omnisubs 失败：" .. tostring(reason))
        mp.osd_message(NAME .. " 失败：" .. tostring(reason)
            .. (attached and "（现在挂着的是这次跑出来的部分字幕）" or ""), 8)
        return
    end

    if not file_exists(srt) then
        mp.osd_message(NAME .. " 没有写出字幕文件：" .. tostring(srt), 8)
        return
    end

    -- 跑完这一下才一定切过去（途中那几次用的是 auto，不抢用户正看的字幕），
    -- 也顺带把最后一次重写读进来 —— 它多半是紧跟结束写在最后那个窗口之后的，没赶上轮询。
    attach(srt, label, true)
    mp.osd_message("字幕已就绪：" .. label, 4)
end

local function start(video, want_video)
    local executable = find_omnisubs()
    if not executable then
        mp.osd_message("找不到 omnisubs.exe；请把它放在 main.lua 旁边", 8)
        return
    end

    local label = want_video and "翻译音轨和画面" or "翻译音轨"
    local progress = progress_path()
    os.remove(progress)

    local args = { executable, video, "--progress-file", progress }

    if want_video then
        table.insert(args, "--model-video")
        table.insert(args, VIDEO_PROFILE)
    end

    local handle
    local ok, err = pcall(function()
        handle = mp.command_native_async({
            name = "subprocess",
            args = args,
            playback_only = false,
        }, finish)
    end)

    if not ok then
        os.remove(progress)
        mp.osd_message("起不了 OmniSubs：" .. tostring(handle or err), 8)
        return
    end

    local srt = srt_path(video)

    job = {
        srt = srt,
        progress = progress,
        label = label,
        handle = handle,
        -- 同名 .srt 现在就有的话，那是上一次跑剩下的、或者用户自己放的一份：记下它现在多大，
        -- 等这次的识别把它改大了才算这次的产物（见 follow）。没被改过就不去动它。
        seen = file_size(srt),
        attached = false,
    }

    -- 开跑时说一句就够了：此后屏幕上不再有常驻的字，字幕会一段段长出来。
    mp.osd_message(NAME .. " 开始生成 · " .. label .. "（字幕会一段段长出来）", 5)
    poll_timer = mp.add_periodic_timer(POLL_INTERVAL, follow)
end

local function cancel()
    if not job then
        mp.osd_message("没有正在生成的字幕", 3)
        return
    end

    local handle = job.handle
    release()

    -- 杀的是 mpv 自己起起来的那个进程。job 已经清掉，所以它的回调只会空跑一次。
    if handle then
        pcall(mp.abort_async_command, handle)
    end

    mp.osd_message("已取消", 3)
end

-- ---------------------------------------------------------------- 入口

local function generate(want_video)
    return function()
        if job then
            mp.osd_message(NAME .. " 已经在生成字幕了", 3)
            return
        end

        local path = mp.get_property("path")
        if not path or path == "" then
            mp.osd_message("没有正在播放的视频", 3)
            return
        end

        local video = absolute(path)
        if not file_exists(video) then
            mp.osd_message("这不是一个本地文件：" .. path, 5)
            return
        end

        start(video, want_video)
    end
end

-- 名字是给菜单用的（见 MENU 里的 script-binding），所以不绑按键：这个插件只用右键菜单。
mp.add_key_binding(nil, "generate-audio", generate(false))
mp.add_key_binding(nil, "generate-video", generate(true))
mp.add_key_binding(nil, "cancel", cancel)

ensure_menu()
mp.observe_property("menu-data", "native", ensure_menu)
