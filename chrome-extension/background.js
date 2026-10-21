import { buildRules, shouldRedirect, serviceGoneTooLong, BLOCK_RULE_ID, FIRST_ALLOW_ID } from "./rules.js";

const API = "http://127.0.0.1:47800/api/v1";
const BLOCK_PAGE = "/blocked.html";

/** Reads the activity from the local Rotina service and applies (or clears) the site rules. */
async function refresh() {
  let state;
  try {
    const res = await fetch(`${API}/state`, { cache: "no-store" });
    if (!res.ok) return;
    state = await res.json();
  } catch {
    // Service unreachable: keep the rules (stopping the service must not lift the block), but not forever,
    // or a broken or removed service would block sites for good.
    const { lastOk = Date.now() } = await chrome.storage.local.get("lastOk");
    if (serviceGoneTooLong(lastOk, Date.now()))
      await chrome.declarativeNetRequest.updateDynamicRules({ removeRuleIds: [BLOCK_RULE_ID, FIRST_ALLOW_ID] });
    return;
  }
  await chrome.storage.local.set({ lastOk: Date.now() });
  // "I am alive": if this stops during an activity (extension disabled), the desktop app closes Chrome.
  fetch(`${API}/heartbeat/extension`, { method: "POST" }).catch(() => {});
  const active = state.state !== "free" && state.allowed && state.session;
  const sites = active ? state.allowed.web : [];
  await chrome.declarativeNetRequest.updateDynamicRules({
    removeRuleIds: [BLOCK_RULE_ID, FIRST_ALLOW_ID],
    addRules: active ? buildRules(sites, BLOCK_PAGE) : [],
  });

  // When a new activity starts, tabs that were already open on other sites go to the block page too.
  const sessionId = active ? state.session.id : null;
  const { lastSessionId = null } = await chrome.storage.session.get("lastSessionId");
  if (sessionId && sessionId !== lastSessionId) {
    for (const tab of await chrome.tabs.query({})) {
      if (tab.id !== undefined && tab.url && shouldRedirect(tab.url, sites))
        await chrome.tabs.update(tab.id, { url: chrome.runtime.getURL(BLOCK_PAGE) });
    }
  }
  if (sessionId !== lastSessionId) await chrome.storage.session.set({ lastSessionId: sessionId });
}

// MV3 workers sleep when idle: the alarm (30 s is Chrome's minimum) wakes it, navigation refreshes
// right away, and while awake it polls every few seconds.
chrome.runtime.onInstalled.addListener(refresh);
chrome.runtime.onStartup.addListener(refresh);
chrome.alarms.create("refresh", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(refresh);
chrome.webNavigation.onBeforeNavigate.addListener((d) => { if (d.frameId === 0) refresh(); });
setInterval(refresh, 3000);
refresh();
