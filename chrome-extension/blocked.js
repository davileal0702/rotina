// Block page: shows the activity, time left until it can end, and the allowed sites.
const $ = (id) => document.getElementById(id);

function clock(seconds) {
  const h = Math.floor(seconds / 3600), m = Math.floor((seconds % 3600) / 60), s = seconds % 60;
  const mm = String(m).padStart(2, "0"), ss = String(s).padStart(2, "0");
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}

// The address that was blocked comes after "#". Only web pages are reopened.
const hash = location.href.indexOf("#");
const original = hash < 0 ? "" : location.href.slice(hash + 1);
const back = /^https?:\/\//i.test(original) ? original : "";

function allowedNow(state) {
  if (state.state === "free" || !state.session) return true;
  try {
    const host = new URL(back).hostname.toLowerCase();
    return state.allowed.web.some((d) => host === d || host.endsWith("." + d));
  } catch { return false; }
}

async function show() {
  let state;
  try {
    state = await (await fetch("http://127.0.0.1:47800/api/v1/state", { cache: "no-store" })).json();
  } catch {
    $("detail").textContent = "Não foi possível falar com o Rotina. Verifique se o serviço está rodando.";
    return;
  }
  // Activity over (or this site now allowed): go back to the page that was blocked.
  if (back && allowedNow(state)) { location.replace(back); return; }
  if (state.state === "free" || !state.session) {
    document.documentElement.style.removeProperty("--accent"); // back to the default color, not the old activity's
    $("title").textContent = "Nenhuma atividade em andamento";
    $("detail").textContent = "Tudo está liberado agora. Volte e abra o site de novo.";
    $("clock").hidden = true;
    $("allowed-section").hidden = true;
    return;
  }
  const activity = state.session.rulesSnapshot;
  document.documentElement.style.setProperty("--accent", activity.color);
  $("title").textContent = `Não está liberado em ${activity.name}`;
  $("clock").hidden = state.state !== "locked";
  $("clock").textContent = clock(state.secondsUntilCompletable);
  $("detail").textContent = state.state === "locked"
    ? "para poder encerrar a atividade."
    : "O tempo mínimo já passou. Você pode encerrar a atividade no Rotina.";
  const list = $("allowed");
  list.replaceChildren(...state.allowed.web.map((d) => Object.assign(document.createElement("li"), { textContent: d })));
  if (state.allowed.web.length === 0) list.replaceChildren(Object.assign(document.createElement("li"), { textContent: "Nenhum site" }));
  $("allowed-section").hidden = false;
}

show();
setInterval(show, 1000);
