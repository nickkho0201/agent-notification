// Wire-проверка MCP stdio без OpenCode и без модели: реальный процесс AgentNotify serve-mcp.
// Запуск: node tests/mcp-wire-test.mjs <path-to-AgentNotify.exe>
import { spawn } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const exe = process.argv[2];
if (!exe) {
  console.error("usage: node tests/mcp-wire-test.mjs <AgentNotify.exe>");
  process.exit(2);
}

const home = mkdtempSync(join(tmpdir(), "agentnotify-mcp-"));
const child = spawn(exe, ["serve-mcp", "--home", home], {
  stdio: ["pipe", "pipe", "pipe"],
  env: { ...process.env, AGENTNOTIFY_HOME: home },
  windowsHide: true,
});

let buffer = "";
const pending = new Map();
let nextId = 1;
const stderr = [];
child.stderr.on("data", (d) => stderr.push(d.toString("utf8")));

const results = [];
function check(name, condition, detail) {
  results.push({ name, ok: !!condition, detail });
  console.log(`${condition ? "PASS" : "FAIL"} ${name}${detail && !condition ? " :: " + detail : ""}`);
}

// Время в тесте не зависит от локальной зоны машины: абсолютный момент (UTC) сериализуется
// с намеренно выбранным смещением +03:00 через UTC-арифметику. Иначе приклеенный к локальным
// часам offset сдвигал бы момент и в поясах восточнее +03 давал бы прошедшее время.
const pad = (n) => String(n).padStart(2, "0");
function isoAt(epochMs) {
  const d = new Date(epochMs + 180 * 60 * 1000);
  return (
    `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}T` +
    `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}+03:00`
  );
}
// Секунда на границе вверх: момент строго в будущем и совпадает с точностью до секунды.
function futureSecond(msFromNow) {
  return Math.ceil((Date.now() + msFromNow) / 1000) * 1000;
}

child.stdout.on("data", (chunk) => {
  buffer += chunk.toString("utf8");
  let index;
  while ((index = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, index).trim();
    buffer = buffer.slice(index + 1);
    if (!line) continue;
    let message;
    try {
      message = JSON.parse(line);
    } catch (e) {
      console.log("FAIL stdout is not JSON:", line.slice(0, 200));
      process.exitCode = 1;
      continue;
    }
    const waiter = pending.get(message.id);
    if (waiter) {
      pending.delete(message.id);
      waiter(message);
    }
  }
});

function request(method, params) {
  const id = nextId++;
  const payload = { jsonrpc: "2.0", id, method, params };
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`timeout ${method}`)), 15000);
    pending.set(id, (message) => {
      clearTimeout(timer);
      resolve(message);
    });
    child.stdin.write(JSON.stringify(payload) + "\n");
  });
}

function notify(method, params) {
  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method, params }) + "\n");
}

function textPayload(response) {
  const text = response?.result?.content?.[0]?.text ?? "";
  try {
    return JSON.parse(text);
  } catch {
    return { raw: text };
  }
}

try {
  const init = await request("initialize", {
    protocolVersion: "2025-06-18",
    capabilities: {},
    clientInfo: { name: "wire-test", version: "1" },
  });
  check("initialize: protocolVersion", init.result?.protocolVersion === "2025-06-18", JSON.stringify(init));
  check("initialize: serverInfo name", init.result?.serverInfo?.name === "agent-notification");
  check("initialize: instructions mention list_reminders", /list_reminders/.test(init.result?.instructions ?? ""));
  notify("notifications/initialized", {});

  const ping = await request("ping", {});
  check("ping", ping.result !== undefined);

  const tools = await request("tools/list", {});
  const names = (tools.result?.tools ?? []).map((t) => t.name).sort();
  check("tools/list: ровно четыре инструмента", names.length === 4, JSON.stringify(names));
  check(
    "tools/list: имена (старые + update_reminder)",
    JSON.stringify(names) === JSON.stringify(["cancel_reminder", "list_reminders", "schedule_reminder", "update_reminder"]),
    JSON.stringify(names),
  );
  const scheduleSchema = tools.result?.tools?.find((t) => t.name === "schedule_reminder")?.inputSchema;
  check("schema: additionalProperties=false", scheduleSchema?.additionalProperties === false);
  check("schema: required at,text", JSON.stringify(scheduleSchema?.required) === JSON.stringify(["at", "text"]));
  const updateSchema = tools.result?.tools?.find((t) => t.name === "update_reminder")?.inputSchema;
  check("update schema: additionalProperties=false", updateSchema?.additionalProperties === false);
  check("update schema: required id", JSON.stringify(updateSchema?.required) === JSON.stringify(["id"]));
  check(
    "update schema: поля id,at,text",
    JSON.stringify(Object.keys(updateSchema?.properties ?? {}).sort()) === JSON.stringify(["at", "id", "text"]),
  );

  const empty = await request("tools/call", { name: "list_reminders", arguments: {} });
  const emptyPayload = empty.result?.structuredContent ?? textPayload(empty);
  check("list пустой", Array.isArray(emptyPayload.reminders) && emptyPayload.reminders.length === 0);
  check("list содержит timeContext с зоной", typeof emptyPayload.timeContext?.timeZoneWindows === "string", JSON.stringify(emptyPayload.timeContext));

  const noOffset = await request("tools/call", {
    name: "schedule_reminder",
    arguments: { at: "2026-11-27T09:30:00", text: "без смещения" },
  });
  check("schedule: время без смещения отклонено", noOffset.result?.isError === true, JSON.stringify(noOffset.result));
  check("schedule: isError объясняет смещение", /смещени/i.test(noOffset.result?.content?.[0]?.text ?? ""));

  const past = await request("tools/call", {
    name: "schedule_reminder",
    arguments: { at: "2020-01-01T10:00:00+03:00", text: "прошлое" },
  });
  check("schedule: прошедшее время отклонено", past.result?.isError === true);

  const extra = await request("tools/call", {
    name: "schedule_reminder",
    arguments: { at: "2030-01-01T10:00:00+03:00", text: "x", command: "calc.exe" },
  });
  check("schedule: произвольный аргумент отклонён", extra.result?.isError === true);

  const future = futureSecond(42 * 60 * 1000);
  const at = isoAt(future);
  const text = 'Тест: <b>&"кавычки"</b>\nвторая строка — Юникод ✓';

  check("time helper: +03 — тот же абсолютный момент", Date.parse(at) === future, JSON.stringify({ at, future: new Date(future).toISOString() }));
  check("time helper: срок в будущем", Date.parse(at) > Date.now(), at);

  const created = await request("tools/call", {
    name: "schedule_reminder",
    arguments: { at, text },
  });
  const createdPayload = created.result?.structuredContent ?? textPayload(created);
  check("schedule: создано", created.result?.isError === false && /^rem_[a-z0-9]+$/.test(createdPayload.id ?? ""), JSON.stringify(created.result));
  check("schedule: windowsTaskRegistered=true", createdPayload.windowsTaskRegistered === true, JSON.stringify(createdPayload));
  check("schedule: dueUtc — запрошенный момент", Date.parse(createdPayload.dueUtc) === Date.parse(at), JSON.stringify({ dueUtc: createdPayload.dueUtc, at }));
  check("schedule: текст сохранён как данные", createdPayload.text === text);
  check("schedule: sweepTaskRegistered — boolean", typeof createdPayload.sweepTaskRegistered === "boolean", JSON.stringify(createdPayload.sweepTaskRegistered));

  const listed = await request("tools/call", { name: "list_reminders", arguments: {} });
  const listedPayload = listed.result?.structuredContent ?? textPayload(listed);
  check("list: напоминание видно", (listedPayload.reminders ?? []).some((r) => r.id === createdPayload.id));
  check("list: unicode/кавычки целы", (listedPayload.reminders ?? [])[0]?.text === text);
  check("list: sweepTaskRegistered — boolean", typeof listedPayload.sweepTaskRegistered === "boolean");
  check("list: returned/total/truncated есть", listedPayload.returned === 1 && listedPayload.total === 1 && listedPayload.truncated === false);
  check("list: nextCursor null при полной выборке", listedPayload.nextCursor === null);
  const firstReminder = (listedPayload.reminders ?? [])[0] ?? {};
  check("list: dueLocal хранит исходное смещение", String(firstReminder.dueLocal ?? "").endsWith("+03:00"), JSON.stringify(firstReminder.dueLocal));
  check(
    "list: dueCurrentZoneLocal — тот же момент",
    Math.abs(new Date(firstReminder.dueCurrentZoneLocal).getTime() - new Date(createdPayload.dueUtc).getTime()) < 1,
    JSON.stringify({ current: firstReminder.dueCurrentZoneLocal, utc: createdPayload.dueUtc }),
  );

  const fractional = await request("tools/call", { name: "list_reminders", arguments: { from: "2026-11-26T00:00:00.0005Z" } });
  check(
    "list: дробная граница округлена вверх до мс",
    fractional.result?.isError === false && fractional.result?.structuredContent?.appliedFilters?.from === "2026-11-26T00:00:00.001Z",
    JSON.stringify(fractional.result?.structuredContent?.appliedFilters),
  );

  const byText = await request("tools/call", { name: "list_reminders", arguments: { text: "КАВЫЧКИ" } });
  const byTextPayload = byText.result?.structuredContent ?? {};
  check("list: поиск по тексту без учёта регистра", (byTextPayload.reminders ?? []).length === 1 && byTextPayload.reminders[0].id === createdPayload.id, JSON.stringify(byTextPayload));
  check("list: appliedFilters.text — эхо", byTextPayload.appliedFilters?.text === "КАВЫЧКИ");

  const noMatch = await request("tools/call", { name: "list_reminders", arguments: { text: "нет такого текста" } });
  check("list: пустой поиск не ошибка", noMatch.result?.isError === false && (noMatch.result?.structuredContent?.reminders ?? []).length === 0 && noMatch.result?.structuredContent?.total === 0);

  const byId = await request("tools/call", { name: "list_reminders", arguments: { id: createdPayload.id } });
  check("list: точный id", (byId.result?.structuredContent?.reminders ?? []).length === 1 && byId.result.structuredContent.reminders[0].id === createdPayload.id);

  const notOverdue = await request("tools/call", { name: "list_reminders", arguments: { overdue: true } });
  check("list: overdue не находит будущее", (notOverdue.result?.structuredContent?.reminders ?? []).length === 0 && notOverdue.result?.structuredContent?.total === 0);

  const fromIso = new Date(Date.now() - 60 * 60 * 1000).toISOString();
  const toIso = new Date(Date.now() + 3 * 60 * 60 * 1000).toISOString();
  const inWindow = await request("tools/call", { name: "list_reminders", arguments: { text: "тест", from: fromIso, to: toIso } });
  check("list: AND text+from+to", (inWindow.result?.structuredContent?.reminders ?? []).length === 1 && inWindow.result.structuredContent.appliedFilters?.from?.endsWith("Z") === true, JSON.stringify(inWindow.result?.structuredContent));

  const badFrom = await request("tools/call", { name: "list_reminders", arguments: { from: "2026-11-26T00:00:00" } });
  check("list: граница без смещения отклонена", badFrom.result?.isError === true);
  const badRange = await request("tools/call", { name: "list_reminders", arguments: { from: toIso, to: fromIso } });
  check("list: from >= to отклонён", badRange.result?.isError === true);
  const badLimit = await request("tools/call", { name: "list_reminders", arguments: { limit: 0 } });
  check("list: limit 0 отклонён", badLimit.result?.isError === true);
  const badCursor = await request("tools/call", { name: "list_reminders", arguments: { cursor: "не-курсор" } });
  check("list: курсор отклонён", badCursor.result?.isError === true);
  const badArg = await request("tools/call", { name: "list_reminders", arguments: { command: "calc.exe" } });
  check("list: произвольный аргумент отклонён", badArg.result?.isError === true);

  const future2 = futureSecond(90 * 60 * 1000);
  const at2 = isoAt(future2);
  const second = await request("tools/call", {
    name: "schedule_reminder",
    arguments: { at: at2, text: "Оплата на этой неделе" },
  });
  const secondPayload = second.result?.structuredContent ?? {};
  check("schedule: создано второе", second.result?.isError === false && /^rem_[a-z0-9]+$/.test(secondPayload.id ?? ""));

  const page1 = await request("tools/call", { name: "list_reminders", arguments: { limit: 1 } });
  const p1 = page1.result?.structuredContent ?? {};
  check("list: limit=1 усекает выборку", (p1.reminders ?? []).length === 1 && p1.total === 2 && p1.truncated === true && typeof p1.nextCursor === "string", JSON.stringify(p1));
  const page2 = await request("tools/call", { name: "list_reminders", arguments: { limit: 1, cursor: p1.nextCursor } });
  const p2 = page2.result?.structuredContent ?? {};
  check("list: nextCursor отдаёт остаток", (p2.reminders ?? []).length === 1 && (p2.reminders ?? [])[0]?.id !== (p1.reminders ?? [])[0]?.id && p2.truncated === false && p2.nextCursor === null, JSON.stringify(p2));

  const unchanged = await request("tools/call", { name: "list_reminders", arguments: {} });
  check("list: поиск не изменил очередь", (unchanged.result?.structuredContent?.reminders ?? []).length === 2 && unchanged.result.structuredContent.reminders[0].state === "pending");

  // update_reminder: текст, затем срок, затем идемпотентный повтор.
  const updateNone = await request("tools/call", { name: "update_reminder", arguments: { id: createdPayload.id } });
  check("update: без at/text — ошибка", updateNone.result?.isError === true);
  const updateEmptyAt = await request("tools/call", {
    name: "update_reminder",
    arguments: { id: createdPayload.id, at: "", text: "x" },
  });
  check("update: явно пустой at отклонён при наличии text", updateEmptyAt.result?.isError === true, JSON.stringify(updateEmptyAt.result));
  const updateNullAt = await request("tools/call", {
    name: "update_reminder",
    arguments: { id: createdPayload.id, at: null, text: "x" },
  });
  check("update: явный null вместо at отклонён", updateNullAt.result?.isError === true, JSON.stringify(updateNullAt.result));
  const updateNullText = await request("tools/call", {
    name: "update_reminder",
    arguments: { id: createdPayload.id, at: "2026-11-27T09:30:00+03:00", text: null },
  });
  check("update: явный null вместо text отклонён", updateNullText.result?.isError === true, JSON.stringify(updateNullText.result));
  const updateNoOffset = await request("tools/call", {
    name: "update_reminder",
    arguments: { id: createdPayload.id, at: "2026-11-27T09:30:00" },
  });
  check("update: время без смещения отклонено", updateNoOffset.result?.isError === true);
  const updateExtra = await request("tools/call", {
    name: "update_reminder",
    arguments: { id: createdPayload.id, text: "x", command: "calc.exe" },
  });
  check("update: произвольный аргумент отклонён", updateExtra.result?.isError === true);
  const updateUnknown = await request("tools/call", {
    name: "update_reminder",
    arguments: { id: "rem_0000000000000000", text: "x" },
  });
  check("update: неизвестный id — ошибка", updateUnknown.result?.isError === true);

  const originalDueUtc = createdPayload.dueUtc;
  const updatedText = await request("tools/call", {
    name: "update_reminder",
    arguments: { id: createdPayload.id, text: "Обновлённый текст <&> ✓" },
  });
  const updatedTextPayload = updatedText.result?.structuredContent ?? {};
  check("update: текст изменён", updatedText.result?.isError === false && updatedTextPayload.changed === true && updatedTextPayload.textChanged === true, JSON.stringify(updatedText.result));
  check("update: текстовая правка не сдвигает срок", updatedTextPayload.dueChanged === false && updatedTextPayload.dueUtc === originalDueUtc, JSON.stringify(updatedTextPayload));
  check("update: id сохранён", updatedTextPayload.id === createdPayload.id);
  check("update: windowsTaskRegistered=true, syncPending=false", updatedTextPayload.windowsTaskRegistered === true && updatedTextPayload.syncPending === false, JSON.stringify(updatedTextPayload));

  const later = futureSecond(120 * 60 * 1000);
  const atLater = isoAt(later);
  const updatedDue = await request("tools/call", {
    name: "update_reminder",
    arguments: { id: createdPayload.id, at: atLater },
  });
  const updatedDuePayload = updatedDue.result?.structuredContent ?? {};
  check("update: срок изменён", updatedDue.result?.isError === false && updatedDuePayload.dueChanged === true && updatedDuePayload.textChanged === false, JSON.stringify(updatedDue.result));
  check("update: новый срок — запрошенный момент", Date.parse(updatedDuePayload.dueUtc) === Date.parse(atLater), JSON.stringify({ dueUtc: updatedDuePayload.dueUtc, at: atLater }));
  check("update: перерегистрация под тем же именем", updatedDuePayload.windowsTaskRegistered === true && updatedDuePayload.syncPending === false && updatedDuePayload.windowsTaskName === createdPayload.windowsTaskName, JSON.stringify(updatedDuePayload));
  check("update: текст сохранён при переносе", updatedDuePayload.text === "Обновлённый текст <&> ✓");

  const updateNoop = await request("tools/call", {
    name: "update_reminder",
    arguments: { id: createdPayload.id, at: atLater, text: "Обновлённый текст <&> ✓" },
  });
  check("update: повтор идемпотентен", updateNoop.result?.isError === false && updateNoop.result?.structuredContent?.changed === false);

  const afterUpdate = await request("tools/call", { name: "list_reminders", arguments: { id: createdPayload.id } });
  const afterUpdateRow = (afterUpdate.result?.structuredContent?.reminders ?? [])[0] ?? {};
  check("update: запись осталась pending и одна", afterUpdate.result?.structuredContent?.total === 1 && afterUpdateRow.state === "pending" && afterUpdateRow.text === "Обновлённый текст <&> ✓", JSON.stringify(afterUpdateRow));

  const cancelUnknown = await request("tools/call", { name: "cancel_reminder", arguments: { id: "rem_0000000000000000" } });
  check("cancel: неизвестный id — ошибка", cancelUnknown.result?.isError === true);

  const badId = await request("tools/call", { name: "cancel_reminder", arguments: { id: "not-an-id" } });
  check("cancel: неверный формат id отклонён", badId.result?.isError === true);

  const cancelled = await request("tools/call", { name: "cancel_reminder", arguments: { id: createdPayload.id } });
  check("cancel: отменено", cancelled.result?.structuredContent?.cancelled === true, JSON.stringify(cancelled.result));
  check("cancel: задача Windows удалена", cancelled.result?.structuredContent?.windowsTaskRemoved === true);

  const updateCancelled = await request("tools/call", {
    name: "update_reminder",
    arguments: { id: createdPayload.id, text: "нельзя" },
  });
  check("update: отменённую запись нельзя изменить", updateCancelled.result?.isError === true, JSON.stringify(updateCancelled.result));

  const cancelled2 = await request("tools/call", { name: "cancel_reminder", arguments: { id: secondPayload.id } });
  check("cancel: второе отменено", cancelled2.result?.structuredContent?.cancelled === true);

  const after = await request("tools/call", { name: "list_reminders", arguments: {} });
  check("list после отмены пуст", (after.result?.structuredContent?.reminders ?? []).length === 0);

  const unknownMethod = await request("nope/method", {});
  check("неизвестный метод — -32601", unknownMethod.error?.code === -32601);

  const unknownTool = await request("tools/call", { name: "run_command", arguments: {} });
  check("неизвестный инструмент отклонён", unknownTool.error?.code === -32602);
} catch (e) {
  console.log("FAIL exception:", e.message);
  process.exitCode = 1;
} finally {
  child.stdin.end();
  await new Promise((resolve) => {
    const timer = setTimeout(() => {
      child.kill();
      resolve();
    }, 5000);
    child.on("exit", () => {
      clearTimeout(timer);
      resolve();
    });
  });
  // Страховка на случай падения до отмены записей: удаляем задачи только в проверенном
  // временном AGENTNOTIFY_HOME (имя задачи включает хэш пути хранилища), Live не затрагивается.
  if (results.some((r) => !r.ok) || process.exitCode) {
    const cleaned = await new Promise((resolve) => {
      const cleanup = spawn(exe, ["uninstall", "--home", home], {
        stdio: "ignore",
        env: { ...process.env, AGENTNOTIFY_HOME: home },
        windowsHide: true,
      });
      const timer = setTimeout(() => {
        cleanup.kill();
        resolve(false);
      }, 30000);
      cleanup.on("error", () => {
        clearTimeout(timer);
        resolve(false);
      });
      cleanup.on("exit", (code) => {
        clearTimeout(timer);
        resolve(code === 0);
      });
    });
    console.log(
      cleaned
        ? "cleanup: задачи временного home удалены после сбоя"
        : "cleanup: не удалось удалить задачи временного home; удалите вручную: " +
            `"${exe}" uninstall --home "${home}"`,
    );
  }
  try {
    rmSync(home, { recursive: true, force: true });
  } catch {}
  if (stderr.length) console.log("--- stderr ---\n" + stderr.join("").slice(0, 2000));
  const failed = results.filter((r) => !r.ok).length;
  console.log(`\nИтого: ${results.length - failed}/${results.length} PASS`);
  if (failed || process.exitCode) process.exitCode = 1;
}
