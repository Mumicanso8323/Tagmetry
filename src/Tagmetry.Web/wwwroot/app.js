﻿let jobId = null;

let inFlight = false;
let backoffMs = 0;

const POLL_RUNNING_MS = 1000; // ジョブ実行中
const POLL_IDLE_MS = 5000;    // ジョブなし/完了
const POLL_HIDDEN_MS = 10000; // タブ非表示
const BACKOFF_MAX_MS = 30000;

async function api(path, opts) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 8000);

  try {
    const r = await fetch(path, { ...opts, signal: controller.signal });
    if (!r.ok) throw new Error(await r.text());
    return await r.json();
  } finally {
    clearTimeout(timeout);
  }
}

function setStatusText(s) {
  document.getElementById("status").textContent = s;
}

document.getElementById("runBtn").onclick = async () => {
  try {
    const inputDir = document.getElementById("inputDir").value.trim();
    if (!inputDir) return alert("inputDir required");

    setStatusText("starting...");
    const res = await api("/api/run", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ inputDir })
    });

    jobId = res.jobId || null;
    backoffMs = 0; // 成功したらバックオフ解除
  } catch (e) {
    alert(String(e));
  }
};

function calcNextInterval(lastState) {
  // タブが非表示なら抑える
  if (document.hidden) return POLL_HIDDEN_MS;

  // 失敗時バックオフ優先
  if (backoffMs > 0) return backoffMs;

  // ジョブ中は短く、そうでなければ長く
  if (lastState === "running") return POLL_RUNNING_MS;
  return POLL_IDLE_MS;
}

async function tick() {
  if (inFlight) {
    // 多重起動防止
    setTimeout(tick, 250);
    return;
  }

  inFlight = true;

  let lastState = null;

  try {
    // status は jobId がある時だけ
    if (jobId) {
      const s = await api("/api/status?jobId=" + encodeURIComponent(jobId));
      lastState = s.state || null;

      setStatusText(`${s.state} ${s.percent ?? ""}% ${s.message ?? ""}`);

      // 完了/失敗なら jobId を外して idle 扱いに（必要なら残してもOK）
      if (s.state === "completed" || s.state === "failed") {
        jobId = null;
      }
    } else {
      setStatusText("idle");
    }

    // log は「ジョブ中」か「ユーザーが見てる（タブ可視）」時だけ取得
    const shouldFetchLog = (lastState === "running") || !document.hidden;
    if (shouldFetchLog) {
      const log = await api("/api/log?lines=200");
      document.getElementById("log").textContent = (log.lines || []).join("\n");
    }

    // 成功したらバックオフ解除
    backoffMs = 0;
  } catch (e) {
    // 失敗時は指数バックオフ（最大30秒）
    backoffMs = backoffMs ? Math.min(BACKOFF_MAX_MS, backoffMs * 2) : 2000;

    // 画面に軽く出す（うざければ消してOK）
    const msg = String(e);
    setStatusText("error (retry in " + Math.round(backoffMs / 1000) + "s)");
    // console には出す
    console.warn(e);
  } finally {
    inFlight = false;
    const next = calcNextInterval(lastState);
    setTimeout(tick, next);
  }
}

// タブが復帰したらすぐ更新したい場合
document.addEventListener("visibilitychange", () => {
  if (!document.hidden) {
    backoffMs = 0;
    // 少し待ってから即時更新
    setTimeout(tick, 50);
  }
});

tick();
