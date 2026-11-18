-- Sönmez Kombo Puan (sunucu scripti)
-- Puanı sunucu plugin'i hesaplar; makas geçişlerini bu script ölçüp sunucuya bildirir.

local SCALE = 0.75
-- Yerlesim: ana panel 'center' veya 'right'; PB paneli 'right' veya 'left'
local MAIN_POS = 'center'
local PB_POS = 'right'
local WHITE = rgbm(1, 1, 1, 1)
-- Yazi tipi (oyuncunun Windows'unda kurulu olmali, yoksa varsayilana doner).
-- Alternatifler: 'Segoe UI Semibold', 'Trebuchet MS', 'Verdana'
local FONT = 'Bahnschrift;Weight=SemiBold'
local FONT_LABEL = 'Bahnschrift;Weight=Medium'

local TARGET_DEFAULT = 200000000
local st = { score = 0, best = 0, rank = 0, prox = 1, combo = 0, lives = 2, time = 0, speedMult = 1, totalMult = 1, comboLeft = 0, target = TARGET_DEFAULT }
local dispScore = 0
local function fmt(n)
  local s = string.format('%.0f', n)
  local r = s:reverse():gsub('(%d%d%d)', '%1,'):reverse()
  return (r:gsub('^,', ''))
end
local multiNames = { [2] = "2'Lİ", [3] = "3'LÜ", [4] = "4'LÜ", [5] = "5'Lİ", [6] = "6'LI" }
-- Alinan puan (sebebiyle birlikte puanin yanina yerlesir): { amt, reason, age, life, crash, multi }
local gain = nil
local gainA = 0 -- 0..1: puan yazisinin sola kayma orani
-- Canli puan listesi (sunucudaki oyuncular): rows = { {score, name, me}, ... }
local live = { n = 0, myRank = 0, rows = {} }

-- Sunucu adresi: oyunun verdigi IP/port; bu fonksiyonlar bazi surumlerde hata verdigi icin pcall ile,
-- olmazsa plugin ayarindan (ServerIp / HttpPort) gomulen degerler kullanilir
local CFG_PORT = tonumber('__HTTP_PORT__') or 0
local CFG_IP = '__SERVER_IP__'
local srvInfo = '-'
local function serverBase()
  local ok1, ip = pcall(function() return ac.getServerIP() end)
  local ok2, port = pcall(function() return ac.getServerHTTPPort() end)
  local e = ''
  if not ok1 then e = e .. 'getServerIP HATA: ' .. tostring(ip) .. ' | '; ip = nil end
  if not ok2 then e = e .. 'getServerHTTPPort HATA: ' .. tostring(port) .. ' | '; port = nil end
  if type(ip) ~= 'string' or ip == '' then
    if CFG_IP ~= '' and not CFG_IP:find('__', 1, true) then ip = CFG_IP else ip = nil end
  end
  if type(port) ~= 'number' or port <= 0 then port = CFG_PORT end
  srvInfo = e .. tostring(ip) .. ':' .. tostring(port)
  if ip and port > 0 then return ip, port end
  return nil
end

ac.OnlineEvent({
  key = ac.StructItem.key('SonmezKomboState'),
  scoreHi = ac.StructItem.int32(),
  scoreLo = ac.StructItem.int32(),
  bestHi = ac.StructItem.int32(),
  bestLo = ac.StructItem.int32(),
  combo = ac.StructItem.int32(),
  lives = ac.StructItem.int32(),
  time = ac.StructItem.int32(),
  speedMult = ac.StructItem.float(),
  totalMult = ac.StructItem.float(),
  comboLeft = ac.StructItem.float(),
  barMax = ac.StructItem.int32(),
  rank = ac.StructItem.int32(),
  prox = ac.StructItem.float(),
}, function(sender, m)
  st.score = m.scoreHi * 1000000000 + m.scoreLo
  st.best = m.bestHi * 1000000000 + m.bestLo
  st.combo, st.lives, st.time = m.combo, m.lives, m.time
  st.speedMult, st.totalMult, st.comboLeft = m.speedMult, m.totalMult, m.comboLeft
  st.target = m.barMax > 0 and m.barMax or TARGET_DEFAULT
  st.rank = m.rank
  st.prox = m.prox
end)

ac.OnlineEvent({
  key = ac.StructItem.key('SonmezKomboPopup'),
  points = ac.StructItem.int32(),
  kind = ac.StructItem.int32(),
  multi = ac.StructItem.int32(),
}, function(sender, m)
  local amt, reason
  if m.kind == 1 then
    amt, reason = 'KAZA', ''
  else
    amt = '+' .. fmt(m.points)
    if m.multi >= 2 then
      reason = (multiNames[m.multi] or (m.multi .. 'X')) .. ' MAKAS'
    else
      reason = 'MAKAS'
    end
  end
  gain = { amt = amt, reason = reason, age = 0, life = 2.6, crash = m.kind == 1, multi = m.kind == 0 and m.multi >= 2 }
end)


-- Sunucudaki oyuncularin anlik puani. Alan adlari siraya gore n00..n16 (C# KomboLive ile ayni).
local function nameOfSession(sid)
  local ok, res = pcall(function()
    local sim = ac.getSim()
    for i = 0, sim.carsCount - 1 do
      local c = ac.getCar(i)
      if c and c.sessionID == sid then return ac.getDriverName(i) end
    end
    return nil
  end)
  if ok and type(res) == 'string' and res ~= '' then return res end
  return '?'
end

ac.OnlineEvent({
  key = ac.StructItem.key('SonmezKomboLive'),
  n00 = ac.StructItem.int32(), -- satir sayisi
  n01 = ac.StructItem.int32(), -- benim siram
  n02 = ac.StructItem.int32(), n03 = ac.StructItem.int32(), n04 = ac.StructItem.int32(),
  n05 = ac.StructItem.int32(), n06 = ac.StructItem.int32(), n07 = ac.StructItem.int32(),
  n08 = ac.StructItem.int32(), n09 = ac.StructItem.int32(), n10 = ac.StructItem.int32(),
  n11 = ac.StructItem.int32(), n12 = ac.StructItem.int32(), n13 = ac.StructItem.int32(),
  n14 = ac.StructItem.int32(), n15 = ac.StructItem.int32(), n16 = ac.StructItem.int32(),
}, function(sender, m)
  local ids = { m.n02, m.n05, m.n08, m.n11, m.n14 }
  local his = { m.n03, m.n06, m.n09, m.n12, m.n15 }
  local los = { m.n04, m.n07, m.n10, m.n13, m.n16 }
  local mine = ac.getCar(0)
  local mySid = mine and mine.sessionID or -1
  local rows = {}
  for i = 1, math.min(5, m.n00) do
    rows[i] = { score = his[i] * 1000000000 + los[i], name = nameOfSession(ids[i]), me = ids[i] == mySid }
  end
  live.n = #rows
  live.myRank = m.n01
  live.rows = rows
end)

local function txt(s, size, cx, cy, col, f)
  ui.pushDWriteFont(f or FONT)
  local sz = ui.measureDWriteText(s, size)
  ui.dwriteDrawText(s, size, vec2(cx - sz.x / 2, cy - sz.y / 2), col)
  ui.popDWriteFont()
end
local function txtL(s, size, x, cy, col, f)
  ui.pushDWriteFont(f or FONT)
  local sz = ui.measureDWriteText(s, size)
  ui.dwriteDrawText(s, size, vec2(x, cy - sz.y / 2), col)
  ui.popDWriteFont()
end
local function txtR(s, size, x, cy, col, f)
  ui.pushDWriteFont(f or FONT)
  local sz = ui.measureDWriteText(s, size)
  ui.dwriteDrawText(s, size, vec2(x - sz.x, cy - sz.y / 2), col)
  ui.popDWriteFont()
end

-- Yazi seritten tasmasin: genislik maxW'yi (olcek disi birim) asarsa fontu kucultur (en fazla base*minK'ye kadar)
local function fitSz(s, base, maxW, S, f, minK)
  ui.pushDWriteFont(f or FONT)
  local sz = ui.measureDWriteText(s, base * S)
  ui.popDWriteFont()
  local w = sz.x / S
  local size = base
  if w > maxW and w > 0 then size = base * maxW / w end
  if minK and size < base * minK then size = base * minK end
  return size
end
local function ease(t)
  t = math.saturate(t)
  return 1 - (1 - t) * (1 - t)
end

-- Yuvarlatilmis koseli konveks cokgen (pts: {x,y} listesi, radii: kose yaricaplari)
local function fillRounded(pts, radii, col)
  local n = #pts
  ui.pathClear()
  for i = 1, n do
    local p0 = pts[(i - 2) % n + 1]
    local p1 = pts[i]
    local p2 = pts[i % n + 1]
    local r = radii[i]
    if r <= 0.5 then
      ui.pathLineTo(vec2(p1[1], p1[2]))
    else
      local d1x, d1y = p0[1] - p1[1], p0[2] - p1[2]
      local l1 = math.sqrt(d1x * d1x + d1y * d1y)
      local d2x, d2y = p2[1] - p1[1], p2[2] - p1[2]
      local l2 = math.sqrt(d2x * d2x + d2y * d2y)
      local rr = math.min(r, l1 / 2, l2 / 2)
      local ax, ay = p1[1] + d1x / l1 * rr, p1[2] + d1y / l1 * rr
      local bx, by = p1[1] + d2x / l2 * rr, p1[2] + d2y / l2 * rr
      for k = 0, 6 do
        local t = k / 6
        local u = 1 - t
        ui.pathLineTo(vec2(u * u * ax + 2 * u * t * p1[1] + t * t * bx,
                           u * u * ay + 2 * u * t * p1[2] + t * t * by))
      end
    end
  end
  ui.pathFillConvex(col)
end

-- Egik dikdortgen. x = sol alt kose, sl = ust kenarin saga kaymasi,
-- r = sol koseler, rr = sag koseler yaricapi (yoksa r ile ayni; 0 = keskin)
local function para(S, x, y, w, h, sl, col, r, rr)
  r = (r or 0) * S
  rr = rr and (rr * S) or r
  local tl = { (x + sl) * S, y * S }
  local tr = { (x + w + sl) * S, y * S }
  local br = { (x + w) * S, (y + h) * S }
  local bl = { x * S, (y + h) * S }
  if (r > 0.5 or rr > 0.5) and ui.pathFillConvex then
    fillRounded({ tl, tr, br, bl }, { r, rr, rr, r }, col)
  else
    ui.drawQuadFilled(vec2(tl[1], tl[2]), vec2(tr[1], tr[2]), vec2(br[1], br[2]), vec2(bl[1], bl[2]), col)
  end
end
local function drawMain(S)
  local DARK = rgbm(0.07, 0.07, 0.09, 0.90)
  local ORANGE = rgbm(1, 0.52, 0.10, 1)
  local GRAY = rgbm(1, 1, 1, 0.65)
  local GREEN = rgbm(0.2, 0.9, 0.3, 1)
  local OFF = rgbm(1, 1, 1, 0.14)

  -- ust satir: Speed / Proximity / Combo + turuncu toplam carpan
  local tabs = {
    { x = 0,   v = string.format('%.1fX', st.speedMult), l = 'Speed' },
    { x = 122, v = string.format('%.1fX', st.prox),      l = 'Proximity' },
    { x = 244, v = fmt(st.combo) .. 'X',                 l = 'Combo' },
  }
  for _, t in ipairs(tabs) do
    para(S, t.x, 0, 118, 52, 16, DARK, 9)
    txt(t.v, 22 * S, (t.x + 67) * S, 20 * S, WHITE)
    txt(t.l, 13 * S, (t.x + 67) * S, 41 * S, GRAY, FONT_LABEL)
  end

  para(S, 366, 0, 140, 52, 16, ORANGE, 9)
  local hx = 366 + 16 * 6 / 52
  para(S, hx + 9, 6, 5, 40, 16 * 40 / 52, rgbm(1, 0.82, 0.55, 0.95), 1.5)
  para(S, hx + 19, 6, 2.5, 40, 16 * 40 / 52, rgbm(1, 0.82, 0.55, 0.95), 1)
  txt(string.format('%.1fX', st.totalMult), 28 * S, 444 * S, 24 * S, WHITE)
  if st.combo > 0 then
    para(S, 388, 44, 112 * math.saturate(st.comboLeft), 5, 1.5, rgbm(1, 1, 1, 0.9), 2)
  end

  -- puan seridi: 200M'a kadar soldan kirmiziya dolar, ucu silik
  local px, py, pw, ph, psl = 8, 60, 500, 52, 18
  para(S, px, py, pw, ph, psl, WHITE, 12)
  local p = math.saturate(dispScore / (st.target > 0 and st.target or TARGET_DEFAULT))
  if p > 0.0005 then
    local rr, rg, rb = 0.93, 0.13, 0.15
    if p >= 0.999 then
      para(S, px, py, pw, ph, psl, rgbm(rr, rg, rb, 1), 12)
    else
      local w = pw * p
      local fade = math.min(w, 90) * math.saturate((1 - p) / 0.05)
      local solid = w - fade
      if solid > 0 then para(S, px, py, solid, ph, psl, rgbm(rr, rg, rb, 1), 12, 0) end
      local N = 28
      for k = 0, N - 1 do
        local a = 1 - (k + 0.5) / N
        para(S, px + solid + fade * k / N, py, fade / N + 0.6, ph, psl, rgbm(rr, rg, rb, a))
      end
    end
  end
  -- puan yazisi: rakam uzadikca kuculur; alinan puan varken sola kayar
  local ptsText = fmt(dispScore) .. ' PTS'
  local pcx = 267 - 82 * gainA
  local pmax = 440 - 130 * gainA
  local pfs = fitSz(ptsText, 34, pmax, S, FONT, 0.35)
  txt(ptsText, pfs * S, pcx * S, (py + ph / 2) * S, rgbm(0.05, 0.05, 0.05, 1))

  -- alinan puan + sebebi: alttan gelip puanin yanina yerlesir
  if gain then
    local inT = ease(gain.age / 0.35)
    local outT = math.saturate((gain.life - gain.age) / 0.5)
    local pa = inT * outT
    local off = (1 - inT) * 34
    local bx, by, bw, bh = px + 338, py + 5, 150, 42
    para(S, bx, by + off, bw, bh, 14.5, rgbm(0.07, 0.07, 0.09, 0.92 * pa), 8)
    local gcx = bx + bw / 2 + 7
    local acol = gain.crash and rgbm(1, 0.3, 0.3, pa) or (gain.multi and rgbm(1, 0.82, 0.2, pa) or rgbm(1, 0.52, 0.10, pa))
    local hasR = gain.reason ~= ''
    local gfs = fitSz(gain.amt, 24, 122, S, FONT, 0.4)
    txt(gain.amt, gfs * S, gcx * S, (by + off + (hasR and 15 or 21)) * S, acol)
    if hasR then
      local rfs = fitSz(gain.reason, 11, 122, S, FONT_LABEL, 0.5)
      txt(gain.reason, rfs * S, gcx * S, (by + off + 33) * S, rgbm(1, 1, 1, 0.7 * pa))
    end
  end

  -- canlar
  for i = 1, 2 do
    para(S, 534 + (i - 1) * 16, 66, 10, 40, 5, (st.lives >= (3 - i)) and GREEN or OFF, 4)
  end
end

local logoUrl = nil
local function getLogoUrl()
  if not logoUrl then
    local ip, port = serverBase()
    if ip then
      logoUrl = string.format('http://%s:%d/sonmezkombo/logo.png', ip, port)
    end
  end
  return logoUrl
end

local function drawPB(S)
  local P = function(x, y) return vec2(x * S, y * S) end
  local DARK = rgbm(0.06, 0.06, 0.08, 0.92)
  local LIGHT = rgbm(0.93, 0.93, 0.93, 1)

  -- ust satir: egik koyu serit + ince acik sekme; ustte kucuk "TOPLAM PUAN", rakam altinda
  para(S, 12, 0, 338, 46, 22, DARK, 8)
  para(S, 12, 0, 14, 46, 22, LIGHT, 8)
  txtL('TOPLAM PUAN', 10 * S, 54 * S, 11 * S, rgbm(1, 1, 1, 0.62), FONT_LABEL)
  local numText = fmt(st.best)
  local nfs = fitSz(numText, 23, 268, S, FONT, 0.4)
  txtL(numText, nfs * S, 54 * S, 30 * S, WHITE)

  -- alt satir: logo + siralama
  local rankText = (st.rank > 0 and ('#' .. fmt(st.rank)) or '#-') .. ' SIRALAMA'
  ui.pushDWriteFont(FONT)
  local sz = ui.measureDWriteText(rankText, 18 * S)
  ui.popDWriteFont()
  local w = 52 + sz.x / S + 14
  ui.drawRectFilled(P(6, 54), P(6 + w, 92), DARK, 10 * S)
  local url = getLogoUrl()
  if url then ui.drawImage(url, P(12, 56), P(48, 92)) end
  txtL(rankText, 18 * S, 56 * S, 73 * S, WHITE)
end

-- Canli puan listesi (kucuk panel): sunucudaki oyuncular, anlik puana gore
local function drawLive(S)
  local DARK = rgbm(0.06, 0.06, 0.08, 0.88)
  local LIGHT = rgbm(0.93, 0.93, 0.93, 1)
  local ORANGE = rgbm(1, 0.52, 0.10, 1)
  local GREEN = rgbm(0.2, 0.9, 0.3, 1)
  local INK = rgbm(0.05, 0.05, 0.05, 1)
  ui.drawRectFilled(vec2(16 * S, 8.5 * S), vec2(21 * S, 13.5 * S), GREEN, 3 * S)
  txtL('CANLI PUAN', 10 * S, 28 * S, 11 * S, rgbm(1, 1, 1, 0.6), FONT_LABEL)

  local pinned = live.myRank > 5 and live.n >= 5
  local y = 22
  for k = 1, live.n do
    local r = live.rows[k]
    local rank = k
    if pinned and k == live.n then
      txt('...', 12 * S, 130 * S, (y + 3) * S, rgbm(1, 1, 1, 0.6), FONT_LABEL)
      y = y + 11
      rank = live.myRank
    end
    para(S, 40, y, 200, 20, 7, DARK, 5)
    para(S, 12, y, 34, 20, 7, r.me and ORANGE or LIGHT, 5)
    local rt = tostring(rank)
    txt(rt, fitSz(rt, 12, 24, S, FONT, 0.5) * S, 32.5 * S, (y + 10) * S, INK)
    local col = r.me and ORANGE or WHITE
    local sc = fmt(r.score)
    local sfs = fitSz(sc, 12, 76, S, FONT, 0.5)
    ui.pushDWriteFont(FONT)
    local ssz = ui.measureDWriteText(sc, sfs * S)
    ui.popDWriteFont()
    txtR(sc, sfs * S, 234 * S, (y + 10) * S, col)
    local nmax = 234 - 60 - ssz.x / S - 8
    txtL(r.name, fitSz(r.name, 12, nmax, S, FONT, 0.55) * S, 60 * S, (y + 10) * S, col)
    y = y + 23
  end
end


-- ===== Makas tespiti (oyuncunun oyununda; AI trafik dahil tum araclar) =====
-- Sunucu 0.0.55'te AI araclarin konumunu plugin'e vermedigi icin gecisi burada olcup
-- sunucuya bildiriyoruz. Puani yine sunucu hesaplar (ayarlar sunucuda).
local DEBUG_UI = false -- sorun giderme: true yapinca ekranin solunda makas tespiti durumunu gosterir
local pass = { prev = {}, last = {}, clock = 0, url = nil, warned = false, pingSent = false }
local dbg = { cars = 0, conn = 0, near = -1, detected = 0, posts = 0, last = '-', url = '-', ipinfo = '-' }

local function passUrl()
  if not pass.url then
    local ip, port = serverBase()
    if ip then
      pass.url = string.format('http://%s:%d/sonmezkombo/pass', ip, port)
    end
  end
  return pass.url
end

local function postText(body)
  local url = passUrl()
  if not url then
    dbg.last = 'URL YOK'
    return
  end
  dbg.url = url
  dbg.posts = dbg.posts + 1
  local ok, e = pcall(function()
    web.post(url, { ['Content-Type'] = 'text/plain' }, body, function(err, resp)
      if err then
        dbg.last = 'HATA: ' .. tostring(err)
        ac.log('SonmezKombo POST hata: ' .. tostring(err))
      else
        dbg.last = 'OK ' .. tostring(resp and resp.status)
      end
    end)
  end)
  if not ok then
    dbg.last = 'web.post cagrisi HATA: ' .. tostring(e)
    ac.log('SonmezKombo web.post hata: ' .. tostring(e))
  end
end

local function sendPasses(list, me)
  local lines = { tostring(me.sessionID or -1), ac.getDriverName(0) or '' }
  for _, p in ipairs(list) do
    lines[#lines + 1] = string.format('%.3f;%.1f;%.1f', p.lat, p.speed, p.oSpeed)
  end
  ac.log('SonmezKombo makas bildirimi: ' .. #list .. ' adet')
  postText(table.concat(lines, '\n'))
end

function script.update(dt)
  local me = ac.getCar(0)
  if not me then return end
  local sim = ac.getSim()
  pass.clock = pass.clock + dt

  local mv = me.velocity
  local vlen = math.sqrt(mv.x * mv.x + mv.z * mv.z)
  local moving = vlen > 1
  local fx, fz = 0, 0
  if moving then fx, fz = mv.x / vlen, mv.z / vlen end
  local speed = me.speedKmh
  local mp = me.position
  local list = nil
  dbg.cars = sim.carsCount
  dbg.conn = 0
  dbg.near = -1

  if not pass.pingSent and pass.clock > 4 then
    pass.pingSent = true
    postText('ping\nping')
  end

  for i = 1, sim.carsCount - 1 do
    local c = ac.getCar(i)
    if c and c.isConnected then
      local cp = c.position
      if not (cp.x == 0 and cp.z == 0) then
        dbg.conn = dbg.conn + 1
        local dd = math.sqrt((cp.x - mp.x) ^ 2 + (cp.z - mp.z) ^ 2)
        if dbg.near < 0 or dd < dbg.near then dbg.near = dd end
      end
      if not (cp.x == 0 and cp.z == 0) then
        local dx, dz = cp.x - mp.x, cp.z - mp.z
        local fwd, lat = 0, 0
        if moving then
          fwd = dx * fx + dz * fz
          lat = dx * fz - dz * fx
        end
        local pv = pass.prev[i]
        if pv == nil then
          pass.prev[i] = { fwd = fwd, lat = lat }
        else
          if moving and pv.fwd > 0 and fwd <= 0 and pv.fwd < 15 and fwd > -15 then
            local t = pv.fwd / (pv.fwd - fwd)
            local latCross = pv.lat + (lat - pv.lat) * t
            local ov = c.velocity
            local ol = math.sqrt(ov.x * ov.x + ov.z * ov.z)
            local oSpeed = c.speedKmh
            local sameDir = oSpeed < 5 or (ol > 0.1 and (ov.x / ol * fx + ov.z / ol * fz) > 0.6)
            local last = pass.last[i]
            local cooled = last == nil or (pass.clock - last) > 2
            if sameDir and cooled and speed >= 40 and math.abs(latCross) < 12 then
              pass.last[i] = pass.clock
              list = list or {}
              list[#list + 1] = { lat = math.abs(latCross), speed = speed, oSpeed = oSpeed }
              dbg.detected = dbg.detected + 1
            end
          end
          pv.fwd, pv.lat = fwd, lat
        end
      else
        pass.prev[i] = nil
      end
    end
  end

  if list then sendPasses(list, me) end
end

function script.drawUI()
  if DEBUG_UI then
    ui.transparentWindow('SonmezKomboDbg', vec2(20, 260), vec2(900, 320), function()
      ui.text('KOMBO v8 | arac ' .. dbg.cars .. ' gorunen ' .. dbg.conn .. ' yakin ' .. string.format('%.0f', dbg.near) .. 'm')
      ui.text('tespit ' .. dbg.detected .. ' gonderim ' .. dbg.posts .. ' | POST: ' .. dbg.last)
      ui.text('IP: ' .. srvInfo)
      ui.text('URL: ' .. dbg.url)
    end)
  end
  local dt = ac.getDeltaT()
  dispScore = dispScore + (st.score - dispScore) * math.min(1, dt * 8)
  if gain then
    gain.age = gain.age + dt
    if gain.age >= gain.life then gain = nil end
  end
  local gtarget = (gain and gain.age < gain.life - 0.4) and 1 or 0
  gainA = gainA + (gtarget - gainA) * math.min(1, dt * 10)

  local scr = ac.getUI().windowSize
  local k0 = scr.y / 1080
  local S = SCALE * k0

  local mw, mh = 570 * S, 215 * S
  local mx = MAIN_POS == 'right' and (scr.x - mw - 30 * k0) or ((scr.x - mw) / 2)
  ui.transparentWindow('SonmezKomboMain', vec2(mx, 20 * k0), vec2(mw, mh), function() drawMain(S) end)

  local pw, ph = 390 * S, 100 * S
  local pbx = PB_POS == 'left' and (30 * k0) or (scr.x - pw - 30 * k0)
  ui.transparentWindow('SonmezKomboPB', vec2(pbx, 20 * k0), vec2(pw, ph), function() drawPB(S) end)

  -- canli puan listesi: PB panelinin altinda
  if live.n > 0 then
    ui.transparentWindow('SonmezKomboLive', vec2(pbx, 20 * k0 + ph + 2 * k0), vec2(270 * S, 170 * S), function() drawLive(S) end)
  end
end
