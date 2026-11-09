// Project Previews companion: tells the Project Previews desktop app which folder/workspace this VS Code
// window has open and the name of its active tab. It writes one small JSON file per window to
// %LOCALAPPDATA%\ProjectPreviews\windows and nothing else: no file contents, no network.
'use strict';
const vscode = require('vscode');
const fs = require('fs');
const path = require('path');
const os = require('os');

let file = null;
let focusedAt = 0;
let timer = null;

function reportDir() {
  const base = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
  return path.join(base, 'ProjectPreviews', 'windows');
}

function uriText(uri) {
  return uri.scheme === 'file' ? uri.fsPath : uri.toString();
}

// Name only (e.g. "index.ts"), never contents.
function activeName() {
  const tab = vscode.window.tabGroups && vscode.window.tabGroups.activeTabGroup && vscode.window.tabGroups.activeTabGroup.activeTab;
  if (tab) {
    const input = tab.input || {};
    const uri = input.uri || input.modified || (input.notebookType && input.uri);
    if (uri && uri.path) return path.posix.basename(uri.path);
    return tab.label || null;
  }
  const ed = vscode.window.activeTextEditor;
  return ed ? path.posix.basename(ed.document.uri.path) : null;
}

function write() {
  if (!file) return;
  const ws = vscode.workspace;
  let workspaceFile = null;
  if (ws.workspaceFile) workspaceFile = ws.workspaceFile.scheme === 'untitled' ? 'untitled' : uriText(ws.workspaceFile);
  const data = {
    v: 1,
    sessionId: vscode.env.sessionId,
    pid: process.pid,
    name: ws.name || '',
    workspaceFile,
    folders: (ws.workspaceFolders || []).map(f => uriText(f.uri)),
    activeFile: activeName(),
    focused: vscode.window.state.focused,
    focusedAt,
    updatedAt: Date.now(),
  };
  try {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const tmp = file + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify(data));
    try { fs.renameSync(tmp, file); } catch { fs.writeFileSync(file, JSON.stringify(data)); try { fs.unlinkSync(tmp); } catch { } }
  } catch {
    // Best effort; the desktop app falls back to window titles.
  }
}

function soon() {
  clearTimeout(timer);
  timer = setTimeout(write, 120);
}

function activate(context) {
  const id = String(vscode.env.sessionId || process.pid).replace(/[^A-Za-z0-9-]/g, '');
  file = path.join(reportDir(), id + '.json');
  if (vscode.window.state.focused) focusedAt = Date.now();
  write();
  context.subscriptions.push(
    vscode.window.onDidChangeWindowState(e => {
      if (e.focused) { focusedAt = Date.now(); write(); } else soon();
    }),
    vscode.window.tabGroups.onDidChangeTabs(soon),
    vscode.window.tabGroups.onDidChangeTabGroups(soon),
    vscode.window.onDidChangeActiveTextEditor(soon),
    vscode.workspace.onDidChangeWorkspaceFolders(soon),
    { dispose: () => clearTimeout(timer) },
  );
}

function deactivate() {
  clearTimeout(timer);
  if (file) { try { fs.unlinkSync(file); } catch { } }
}

module.exports = { activate, deactivate };
