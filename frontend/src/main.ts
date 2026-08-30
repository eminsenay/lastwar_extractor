import "./styles.css";
import { open, save } from "@tauri-apps/plugin-dialog";
import { BackendClient } from "./lib/backendClient";
import type { AppState, EventEnvelope, Observation, WorkflowTab } from "./types/protocol";

const client = new BackendClient();
let activeTab: WorkflowTab = "settings";
let rosterSourceType: "xlsx" | "google_sheet" =
  (localStorage.getItem("lastwar_roster_source_type") as "xlsx" | "google_sheet") || "xlsx";
let rosterXlsxPath = localStorage.getItem("lastwar_roster_xlsx_path") || "";
let rosterGoogleSheetUrl = localStorage.getItem("lastwar_roster_google_url") || "";
let rosterSheetName = localStorage.getItem("lastwar_roster_sheet_name") || "Members";
let rosterStatus: "idle" | "loading" | "success" | "error" = "idle";
let rosterStatusMessage = "";
let rosterWarnings: string[] = [];
let rosterLoadedAt: Date | null = null;
let extractionStatus = "idle";
let backendStatus: "connecting" | "ready" | "error" = "connecting";
let backendError = "";
let settingsStatus: "idle" | "saving" | "saved" | "error" = "idle";
let settingsMessage = "";
let extractionProgress = { completed: 0, total: 0 };
let reviewingObservationId: string | null = null;
let state: AppState = {
  config: {
    model: "", baseUrl: "", apiStyle: "responses", requestsPerMinute: 28, useCache: true,
    apiKeyPresent: false, apiKeyHint: "", apiKeyRequired: true,
  },
  members: [], memberSource: "", memberWarnings: [], screenshots: [], extractions: [],
  observations: [], issues: [],
  summary: { memberCount: 0, screenshotCount: 0, observationCount: 0, unmatchedCount: 0,
    failedFileCount: 0, avatarMemberCount: 0, avatarSampleCount: 0 },
};

const app = document.querySelector<HTMLDivElement>("#app")!;

function syncRosterInputsFromDOM(): void {
  const rosterInput = document.querySelector<HTMLInputElement>("#roster");
  if (rosterInput) {
    const val = rosterInput.value;
    if (rosterSourceType === "xlsx") {
      rosterXlsxPath = val;
      localStorage.setItem("lastwar_roster_xlsx_path", val);
    } else {
      rosterGoogleSheetUrl = val;
      localStorage.setItem("lastwar_roster_google_url", val);
    }
  }
  const sheetInput = document.querySelector<HTMLInputElement>("#sheet");
  if (sheetInput) {
    rosterSheetName = sheetInput.value;
    localStorage.setItem("lastwar_roster_sheet_name", rosterSheetName);
  }
}

function render(): void {
  const steps: [WorkflowTab, string, string][] = [
    ["settings", "01", "Settings"], ["setup", "02", "Roster"],
    ["import", "03", "Screenshots"], ["review", "04", "Review"],
    ["export", "05", "Export"],
  ];
  app.innerHTML = `
    <main class="shell">
      <header class="topbar">
        <div><span class="kicker">LAST WAR / WEEKLY OPS</span><h1>Score extraction desk</h1></div>
        <div class="connection connection-${backendStatus}" title="${escapeHtml(backendError)}"><span class="dot"></span> ${
          backendStatus === "ready" ? "Backend ready" : backendStatus === "connecting" ? "Connecting to backend..." : "Backend unavailable"
        }</div>
      </header>
      <nav class="steps">${steps.map(([tab, number, label]) => `
        <button class="step ${activeTab === tab ? "active" : ""}" data-tab="${tab}">
          <span>${number}</span><strong>${label}</strong>
        </button>`).join("")}</nav>
      <section class="workspace">${renderTab()}</section>
    </main>`;
  app.querySelectorAll<HTMLButtonElement>("[data-tab]").forEach((button) => {
    button.onclick = () => {
      syncRosterInputsFromDOM();
      activeTab = button.dataset.tab as WorkflowTab;
      settingsStatus = "idle";
      render();
    };
  });
  wireActions();
}

function renderTab(): string {
  if (activeTab === "settings") {
    const config = state.config;
    return `<div class="intro"><span class="eyebrow">01 / endpoint</span><h2>Point the extractor at your model.</h2><p>Works with OpenAI, or any OpenAI-compatible endpoint including local servers.</p><div class="panel form-panel">
      <label>Base URL <input id="cfg-base-url" value="${escapeHtml(config.baseUrl)}" placeholder="https://api.openai.com/v1" /></label>
      <label>Model <input id="cfg-model" value="${escapeHtml(config.model)}" placeholder="gpt-5.6-terra" /></label>
      <label>API style <select id="cfg-api-style">
        <option value="responses" ${config.apiStyle === "responses" ? "selected" : ""}>Responses API (OpenAI)</option>
        <option value="chat" ${config.apiStyle === "chat" ? "selected" : ""}>Chat Completions API (local / compatible)</option>
      </select></label>
      <label>Requests per minute <input id="cfg-rpm" type="number" min="1" max="30" value="${config.requestsPerMinute}" /></label>
      <label class="remember-alias"><input type="checkbox" id="cfg-use-cache" ${config.useCache ? "checked" : ""} /> Reuse cached extractions (skips repeat API calls)</label>
      <div class="status-panel ${config.apiKeyPresent || !config.apiKeyRequired ? "status-success" : "status-error"}"><div class="status-content"><span class="icon">${config.apiKeyPresent || !config.apiKeyRequired ? "✓" : "✗"}</span> <span>${
        config.apiKeyPresent
          ? `<strong>API key detected:</strong> ${escapeHtml(config.apiKeyHint)}`
          : config.apiKeyRequired
            ? `<strong>No API key found.</strong> Set <code>OPENAI_API_KEY</code> in your environment or in a <code>.env</code> file next to the app, then restart.`
            : `<strong>Local endpoint</strong> — no API key required.`
      }<div class="status-meta">For security the key is never stored or edited here. It is read from the <code>OPENAI_API_KEY</code> environment variable at startup. Defaults for the fields above come from <code>OPENAI_BASE_URL</code>, <code>OPENAI_MODEL</code>, <code>OPENAI_API_STYLE</code> and <code>OPENAI_RPM</code>.</div></span></div></div>
      <button class="primary" id="save-settings" ${settingsStatus === "saving" ? "disabled" : ""}>Save settings <span>→</span></button>${
        settingsStatus === "saved" ? `<div class="status-panel status-success"><div class="status-content"><span class="icon">✓</span> <span>Settings saved. Changing the model or endpoint starts a fresh extraction cache.</span></div></div>`
        : settingsStatus === "error" ? `<div class="status-panel status-error"><div class="status-content"><span class="icon">✗</span> <span>${escapeHtml(settingsMessage)}</span></div></div>` : ""
      }</div></div>`;
  }

  if (activeTab === "setup") {
    const currentPath = rosterSourceType === "xlsx" ? rosterXlsxPath : rosterGoogleSheetUrl;
    return `<div class="intro"><span class="eyebrow">02 / roster</span><h2>Start with the source of truth.</h2><p>Load the active member roster before importing leaderboard captures.</p><div class="panel form-panel"><div class="source-toggle" role="group" aria-label="Roster source"><button class="source-option ${rosterSourceType === "xlsx" ? "selected" : ""}" id="source-xlsx">Local Excel</button><button class="source-option ${rosterSourceType === "google_sheet" ? "selected" : ""}" id="source-google">Google Sheet URL</button></div><label>${rosterSourceType === "xlsx" ? "Roster file" : "Google Sheet URL"} <div class="input-row"><input id="roster" value="${escapeHtml(currentPath)}" placeholder="${rosterSourceType === "xlsx" ? "Path to .xlsx workbook" : "https://docs.google.com/spreadsheets/d/..."}" />${rosterSourceType === "xlsx" ? '<button class="secondary compact" id="browse-roster">Browse</button>' : ""}</div></label><label>Worksheet <input id="sheet" value="${escapeHtml(rosterSheetName || "Members")}" /></label><button class="primary" id="load" ${rosterStatus === "loading" ? "disabled" : ""}>Load roster <span>→</span></button>${
      rosterStatus !== "idle" ? `<div class="status-panel status-${rosterStatus}"><div class="status-content">${
        rosterStatus === "loading" ? '<span class="spinner">⟳</span> <span>Loading roster...</span>' : 
        rosterStatus === "success" ? `<span class="icon">✓</span> <span><strong>${rosterStatusMessage}</strong>${
          rosterWarnings.length || rosterLoadedAt ? `<div class="status-meta">${rosterLoadedAt ? `Last loaded: ${getTimeAgo(rosterLoadedAt)}` : ""}</div>${
            rosterWarnings.length ? `<div class="status-warnings">${rosterWarnings.map(w => `<div>⚠ ${escapeHtml(w)}</div>`).join("")}</div>` : ""
          }` : ""
        }</span>` :
        `<span class="icon">✗</span> <span><strong>Error:</strong> ${escapeHtml(rosterStatusMessage)}</span>`
      }</div></div>` : ""
    }</div></div>`;
  }

  if (activeTab === "import") return `<div class="intro"><span class="eyebrow">03 / captures</span><h2>Bring the week into focus.</h2><p>Add screenshots or a folder. Cached captures stay local and skip another request.</p><div class="panel form-panel"><div class="button-row"><button class="primary" id="browse-images">Choose screenshots <span>→</span></button><button class="secondary" id="browse-folder">Choose folder</button></div><label>Selected paths <textarea id="screenshots" placeholder="Or paste one absolute path per line">${escapeHtml(state.screenshots.join("\n"))}</textarea></label><div class="button-row"><button class="primary" id="add">Add screenshots <span>→</span></button><button class="secondary" id="extract" ${extractionStatus === "running" ? "disabled" : ""}>${extractionStatus === "running" ? `Extracting ${extractionProgress.completed}/${extractionProgress.total}` : `Extract ${state.summary.screenshotCount || "all"}`}</button>${extractionStatus === "running" ? '<button class="secondary" id="cancel">Cancel</button>' : ""}</div><div class="queue">${state.screenshots.length ? state.screenshots.map((path) => `<div><span class="file-dot"></span>${escapeHtml(path.split(/[\\/]/).pop() ?? path)}</div>`).join("") : "No screenshots queued yet."}</div></div></div>`;
  if (activeTab === "review") {
    const isUnresolved = (obs: Observation): boolean => !obs.matchedMemberId || obs.matchMethod === "needs_review";
    const ordered = [...state.observations].sort(
      (a, b) => Number(isUnresolved(b)) - Number(isUnresolved(a))
    );
    const reviewing = reviewingObservationId ? state.observations.find((o) => o.id === reviewingObservationId) : null;
    return `<div class="review-head"><div><span class="eyebrow">04 / exceptions</span><h2>Resolve what needs a human.</h2></div><span class="counter">${
      state.summary.failedFileCount ? `<span class="counter-error">${state.summary.failedFileCount} file${state.summary.failedFileCount === 1 ? "" : "s"} failed</span> · ` : ""
    }${state.summary.observationCount} observations · ${state.summary.unmatchedCount} unresolved</span></div>${
      state.issues.length ? `<details class="issues-panel"><summary>${state.issues.length} extraction note${state.issues.length === 1 ? "" : "s"}</summary><ul>${
        state.issues.map((issue) => `<li class="${issue.includes("extraction failed") ? "issue-error" : ""}">${escapeHtml(issue)}</li>`).join("")
      }</ul></details>` : ""
    }<div class="review-list">${
      ordered.length ? ordered.map((item) => `<article class="review-item ${item.matchedMemberId ? "" : "needs-review"}" data-observation="${escapeHtml(item.id)}"><div><span class="eyebrow">${item.day} · rank ${item.rank}</span><h3>${escapeHtml(item.rawName)}</h3><p>${item.points.toLocaleString()} points · ${item.matchMethod} (${(item.matchConfidence * 100).toFixed(0)}%)</p></div><div class="review-controls"><strong>${item.matchedMemberName ? escapeHtml(item.matchedMemberName) : "Unassigned"}</strong><button class="secondary compact" data-action="assign" data-id="${escapeHtml(item.id)}">${item.matchedMemberId ? "Reassign" : "Assign"}</button></div></article>`).join("")
      : `<div class="empty">No observations yet. Extract screenshots on the Screenshots tab first.</div>`
    }</div>${
      reviewing ? `<div class="modal-overlay" id="assign-modal"><div class="modal-panel"><h3>Assign: ${escapeHtml(reviewing.rawName)}</h3><p>${reviewing.points} points · ${reviewing.day}</p><div class="alternatives"><h4>Suggestions</h4>${
        reviewing.alternatives.length ? reviewing.alternatives.map((alt) => `<button class="alternative-option" data-member="${alt.memberId}"><strong>${escapeHtml(alt.name)}</strong> (${(alt.score * 100).toFixed(0)}%)</button>`).join("") : "<p>No suggestions.</p>"
      }</div><div class="member-search"><input id="member-search" type="text" placeholder="Search members..." /><div id="search-results" class="search-results"></div></div><label class="remember-alias"><input type="checkbox" id="remember-alias" checked /> Remember as alias</label><div class="modal-actions"><button class="primary" id="confirm-assign">Assign</button><button class="secondary" id="cancel-assign">Cancel</button></div></div></div>` : ""
    }`;
  }
  if (activeTab === "export") return `<div class="intro"><span class="eyebrow">05 / delivery</span><h2>Ready for the weekly workbook?</h2><p>Export only after the unresolved count and extraction warnings look intentional.</p><div class="metric-grid"><div><span>Members</span><strong>${state.summary.memberCount}</strong></div><div><span>Observations</span><strong>${state.summary.observationCount}</strong></div><div class="warning"><span>Unresolved</span><strong>${state.summary.unmatchedCount}</strong></div></div><button class="primary" id="export">Export workbook <span>→</span></button></div>`;
  return "";
}

function wireActions(): void {
  document.querySelector<HTMLButtonElement>("#save-settings")?.addEventListener("click", async () => {
    settingsStatus = "saving";
    render();
    try {
      state = await client.request<AppState>("set_config", {
        baseUrl: document.querySelector<HTMLInputElement>("#cfg-base-url")?.value.trim() ?? "",
        model: document.querySelector<HTMLInputElement>("#cfg-model")?.value.trim() ?? "",
        apiStyle: document.querySelector<HTMLSelectElement>("#cfg-api-style")?.value ?? "responses",
        requestsPerMinute: Number(document.querySelector<HTMLInputElement>("#cfg-rpm")?.value ?? 28),
        useCache: document.querySelector<HTMLInputElement>("#cfg-use-cache")?.checked ?? true,
      });
      settingsStatus = "saved";
    } catch (error) {
      settingsStatus = "error";
      settingsMessage = error instanceof Error ? error.message : String(error);
    }
    render();
  });

  // Review tab modal assignment
  document.querySelectorAll<HTMLButtonElement>('[data-action="assign"]').forEach((button) => {
    button.addEventListener("click", () => {
      const obsId = button.dataset.id;
      if (obsId) reviewingObservationId = obsId;
      render();
    });
  });
  document.querySelector<HTMLButtonElement>("#cancel-assign")?.addEventListener("click", () => {
    reviewingObservationId = null;
    sessionStorage.removeItem("selectedMemberId");
    render();
  });
  const memberSearchInput = document.querySelector<HTMLInputElement>("#member-search");
  if (memberSearchInput && reviewingObservationId) {
    const resultsDiv = document.querySelector<HTMLDivElement>("#search-results");
    memberSearchInput.addEventListener("input", () => {
      const query = memberSearchInput.value.toLowerCase();
      const results = query.trim() ? state.members.filter((m) => m.name.toLowerCase().includes(query) || m.id.toString().includes(query)).slice(0, 10) : [];
      if (resultsDiv) {
        resultsDiv.innerHTML = results.map((m) => `<button class="search-result" data-member="${m.id}"><strong>${escapeHtml(m.name)}</strong> (ID: ${m.id})</button>`).join("");
        resultsDiv.querySelectorAll<HTMLButtonElement>(".search-result").forEach((btn) => {
          btn.addEventListener("click", () => { sessionStorage.setItem("selectedMemberId", btn.dataset.member || ""); render(); });
        });
      }
    });
  }
  document.querySelectorAll<HTMLButtonElement>(".alternative-option").forEach((btn) => {
    btn.addEventListener("click", () => { sessionStorage.setItem("selectedMemberId", btn.dataset.member || ""); render(); });
  });
  document.querySelector<HTMLButtonElement>("#confirm-assign")?.addEventListener("click", async () => {
    const selectedId = sessionStorage.getItem("selectedMemberId");
    if (!selectedId || !reviewingObservationId) return;
    const rememberAlias = (document.querySelector<HTMLInputElement>("#remember-alias")?.checked) ?? true;
    try {
      state = await client.request<AppState>("assign_observation", { observationId: reviewingObservationId, memberId: parseInt(selectedId), rememberAlias });
      sessionStorage.removeItem("selectedMemberId");
      reviewingObservationId = null;
      render();
    } catch (error) { window.alert(error instanceof Error ? error.message : String(error)); }
  });
  const rosterInput = document.querySelector<HTMLInputElement>("#roster");
  if (rosterInput) {
    rosterInput.addEventListener("input", () => {
      const val = rosterInput.value;
      if (rosterSourceType === "xlsx") {
        rosterXlsxPath = val;
        localStorage.setItem("lastwar_roster_xlsx_path", val);
      } else {
        rosterGoogleSheetUrl = val;
        localStorage.setItem("lastwar_roster_google_url", val);
      }
    });
  }
  const sheetInput = document.querySelector<HTMLInputElement>("#sheet");
  if (sheetInput) {
    sheetInput.addEventListener("input", () => {
      rosterSheetName = sheetInput.value;
      localStorage.setItem("lastwar_roster_sheet_name", rosterSheetName);
    });
  }
  document.querySelector<HTMLButtonElement>("#source-xlsx")?.addEventListener("click", () => {
    syncRosterInputsFromDOM();
    rosterSourceType = "xlsx";
    localStorage.setItem("lastwar_roster_source_type", "xlsx");
    render();
  });
  document.querySelector<HTMLButtonElement>("#source-google")?.addEventListener("click", () => {
    syncRosterInputsFromDOM();
    rosterSourceType = "google_sheet";
    localStorage.setItem("lastwar_roster_source_type", "google_sheet");
    render();
  });
  document.querySelector<HTMLButtonElement>("#browse-roster")?.addEventListener("click", async () => {
    const selected = await open({ multiple: false, directory: false, filters: [{ name: "Excel workbook", extensions: ["xlsx"] }] });
    if (typeof selected === "string") {
      rosterXlsxPath = selected;
      localStorage.setItem("lastwar_roster_xlsx_path", selected);
      render();
    }
  });
  document.querySelector<HTMLButtonElement>("#load")?.addEventListener("click", async () => {
    syncRosterInputsFromDOM();
    const source = (rosterSourceType === "xlsx" ? rosterXlsxPath : rosterGoogleSheetUrl).trim();
    if (!source) return;
    const sheetName = (rosterSheetName || "Members").trim() || "Members";
    rosterStatus = "loading";
    rosterStatusMessage = "";
    rosterWarnings = [];
    render();
    try {
      state = await client.request<AppState>("load_members", {
        sourceType: rosterSourceType,
        source,
        sheetName,
      });
      rosterStatus = "success";
      rosterStatusMessage = `Loaded ${state.summary.memberCount} members from ${state.memberSource}`;
      rosterWarnings = state.memberWarnings;
      rosterLoadedAt = new Date();
    } catch (error) {
      rosterStatus = "error";
      rosterStatusMessage = error instanceof Error ? error.message : String(error);
      rosterWarnings = [];
    }
    render();
  });
  document.querySelector<HTMLButtonElement>("#browse-images")?.addEventListener("click", async () => {
    const selected = await open({ multiple: true, directory: false, filters: [{ name: "Screenshots", extensions: ["png", "jpg", "jpeg", "webp", "gif"] }] });
    if (Array.isArray(selected)) setScreenshotInput(selected);
  });
  document.querySelector<HTMLButtonElement>("#browse-folder")?.addEventListener("click", async () => {
    const selected = await open({ multiple: false, directory: true });
    if (typeof selected === "string") setScreenshotInput([selected]);
  });
  document.querySelector<HTMLButtonElement>("#add")?.addEventListener("click", async () => {
    const paths = document.querySelector<HTMLTextAreaElement>("#screenshots")?.value.split(/\r?\n/).map((path) => path.trim()).filter(Boolean) ?? [];
    if (!paths.length) return;
    try { state = await client.request<AppState>("add_screenshots", { paths }); render(); }
    catch (error) { window.alert(error instanceof Error ? error.message : String(error)); }
  });
  document.querySelector<HTMLButtonElement>("#extract")?.addEventListener("click", async () => {
    extractionStatus = "running";
    extractionProgress = { completed: 0, total: state.screenshots.length };
    render();
    try { await client.request("extract", { operationId: `extract-${Date.now()}` }); }
    catch (error) { extractionStatus = "error"; window.alert(error instanceof Error ? error.message : String(error)); render(); }
  });
  document.querySelector<HTMLButtonElement>("#cancel")?.addEventListener("click", () => { void client.request("cancel"); });
  document.querySelector<HTMLButtonElement>("#export")?.addEventListener("click", async () => {
    if (state.summary.memberCount === 0) { window.alert("Load members first"); return; }
    if (state.summary.observationCount === 0) { window.alert("Extract screenshots first"); return; }
    try {
      const now = new Date();
      const dateStr = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
      const defaultName = `weekly_scores_${dateStr}.xlsx`;
      const path = await save({ defaultPath: defaultName, filters: [{ name: "Excel workbook", extensions: ["xlsx"] }] });
      if (path) {
        const result = await client.request<{ path: string; message: string }>("export", { outputPath: path });
        window.alert(`Exported to ${result.path}`);
      }
    } catch (error) { window.alert(error instanceof Error ? error.message : String(error)); }
  });
}

function setScreenshotInput(paths: string[]): void {
  const input = document.querySelector<HTMLTextAreaElement>("#screenshots");
  if (input) input.value = paths.join("\n");
}

function getTimeAgo(date: Date): string {
  const now = new Date();
  const seconds = Math.floor((now.getTime() - date.getTime()) / 1000);
  if (seconds < 60) return "just now";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} minute${minutes > 1 ? "s" : ""} ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} hour${hours > 1 ? "s" : ""} ago`;
  const days = Math.floor(hours / 24);
  return `${days} day${days > 1 ? "s" : ""} ago`;
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[character] ?? character);
}

client.onEvent((message: EventEnvelope) => {
  if (message.event === "extraction_progress") {
    extractionProgress = message.payload as { completed: number; total: number };
    render();
  }
  if (message.event === "extraction_finished" || message.event === "cancelled") {
    state = message.payload as AppState;
    extractionStatus = message.event === "cancelled" ? "cancelled" : "complete";
    render();
  }
  if (message.event === "error") {
    extractionStatus = "error";
    const detail = message.payload as { message?: string } | undefined;
    render();
    window.alert(detail?.message ? `Extraction failed: ${detail.message}` : "Extraction failed.");
  }
});

async function connectBackend(): Promise<void> {
  try {
    state = await client.request<AppState>("get_state");
    backendStatus = "ready";
    if (state.config.rosterSourceType) {
      rosterSourceType = state.config.rosterSourceType;
      localStorage.setItem("lastwar_roster_source_type", rosterSourceType);
    }
    if (state.config.rosterXlsxPath) {
      rosterXlsxPath = state.config.rosterXlsxPath;
      localStorage.setItem("lastwar_roster_xlsx_path", rosterXlsxPath);
    }
    if (state.config.rosterGoogleSheetUrl) {
      rosterGoogleSheetUrl = state.config.rosterGoogleSheetUrl;
      localStorage.setItem("lastwar_roster_google_url", rosterGoogleSheetUrl);
    }
    if (state.config.rosterSheetName) {
      rosterSheetName = state.config.rosterSheetName;
      localStorage.setItem("lastwar_roster_sheet_name", rosterSheetName);
    }
  } catch (error) {
    backendStatus = "error";
    backendError = error instanceof Error ? error.message : String(error);
  }
  render();
}

render();
void connectBackend();
