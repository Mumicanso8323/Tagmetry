(() => {
  const POLL_RUNNING_MS = 1000;
  const POLL_IDLE_MS = 5000;

  let currentJobId = null;

  const el = {
    status: document.getElementById("status"),
    inputDir: document.getElementById("inputDir"),
    runBtn: document.getElementById("runBtn"),
    cancelBtn: document.getElementById("cancelBtn"),
    log: document.getElementById("log"),
    telemetryToggle: document.getElementById("telemetryToggle"),
    telemetryState: document.getElementById("telemetryState"),
    themeToggle: document.getElementById("themeToggle")
  };

  async function api(path, options) {
    const res = await fetch(path, options);
    if (!res.ok) {
      throw new Error(await res.text());
    }
    return await res.json();
  }

  function stateToText(state) {
    if (typeof state === "string") return state;
    const map = ["Queued", "Running", "Completed", "Failed", "Canceled"];
    return map[state] || String(state);
  }

  function renderStatus(status) {
    const state = stateToText(status.state);
    el.status.textContent = `${state} ${status.percent}% - ${status.message}`;
    if (el.cancelBtn) {
      el.cancelBtn.disabled = !status.isCancelable;
    }
  }

  function renderLogs(logs) {
    el.log.textContent = (logs || [])
      .map((x) => `${x.atUtc} [${x.level}] ${x.message}`)
      .join("\n");
  }

  async function refreshSettings() {
    const settings = await api("/settings");
    if (el.telemetryToggle) el.telemetryToggle.checked = !!settings.telemetryEnabled;
    if (el.telemetryState) {
      el.telemetryState.textContent = settings.telemetryEnabled
        ? "Telemetry is ON (opt-in)."
        : "Telemetry is OFF (default).";
    }
  }

  async function refreshJob() {
    if (!currentJobId) {
      setTimeout(refreshJob, POLL_IDLE_MS);
      return;
    }

    try {
      const status = await api(`/jobs/${encodeURIComponent(currentJobId)}`);
      renderStatus(status);

      const logsRes = await api(`/jobs/${encodeURIComponent(currentJobId)}/logs`);
      renderLogs(logsRes.logs);

      if (["Completed", "Failed", "Canceled"].includes(stateToText(status.state))) {
        currentJobId = null;
      }
    } catch (error) {
      el.status.textContent = `Error: ${String(error)}`;
    } finally {
      setTimeout(refreshJob, POLL_RUNNING_MS);
    }
  }

  async function runJob() {
    const inputDir = el.inputDir.value.trim();
    if (!inputDir) {
      alert("Input directory is required.");
      return;
    }

    const res = await api("/jobs", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ inputDir })
    });

    currentJobId = res.jobId;
    el.status.textContent = "Job submitted.";
    refreshJob();
  }

  async function cancelJob() {
    if (!currentJobId) return;
    await api(`/jobs/${encodeURIComponent(currentJobId)}/cancel`, { method: "POST" });
  }

  async function setTelemetry() {
    await api("/settings/telemetry", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: !!el.telemetryToggle.checked })
    });
    await refreshSettings();
  }

  const themeStorageKey = "tagmetry.theme";
  function initTheme() {
    const saved = localStorage.getItem(themeStorageKey);
    if (saved) document.documentElement.setAttribute("data-theme", saved);
    el.themeToggle?.addEventListener("click", () => {
      const current = document.documentElement.getAttribute("data-theme");
      const next = current === "dark" ? "light" : "dark";
      document.documentElement.setAttribute("data-theme", next);
      localStorage.setItem(themeStorageKey, next);
    });
  }

  el.runBtn?.addEventListener("click", runJob);
  el.cancelBtn?.addEventListener("click", cancelJob);
  el.telemetryToggle?.addEventListener("change", setTelemetry);

  initTheme();
  refreshSettings();
  refreshJob();
})();
