const { spawn } = require("child_process");

class PythonWorker {
  constructor(command, cwd, spawnProcess = spawn) {
    this.command = command;
    this.cwd = cwd;
    this.spawnProcess = spawnProcess;
    this.child = null;
    this.queue = [];
    this.active = null;
    this.sequence = 0;
  }

  run(args, timeoutMs) {
    return new Promise((resolve, reject) => {
      this.queue.push({ id: ++this.sequence, args, timeoutMs, resolve, reject });
      this.dispatch();
    });
  }

  start() {
    const command = this.command();
    const child = this.spawnProcess(command.exe, [...command.prefix, "-u", "-m", "court_creator.service"], {
      cwd: this.cwd,
      windowsHide: true,
    });
    this.child = child;
    let stdout = "";
    let stderr = "";
    const fail = (error) => {
      if (this.child !== child) return;
      this.stop(new Error(stderr.trim() ? `${error.message}: ${stderr.trim().slice(-2000)}` : error.message));
    };
    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString();
      let end;
      while ((end = stdout.indexOf("\n")) >= 0) {
        const line = stdout.slice(0, end).trim();
        stdout = stdout.slice(end + 1);
        if (!line) continue;
        let message;
        try { message = JSON.parse(line); }
        catch { fail(new Error("Rendering engine returned an invalid response.")); return; }
        const pending = this.active;
        if (!pending || message.id !== pending.id) continue;
        clearTimeout(pending.timer);
        this.active = null;
        if (message.error) pending.reject(new Error(message.error));
        else pending.resolve(message.result);
        this.dispatch();
      }
    });
    child.stderr.on("data", (chunk) => { stderr = `${stderr}${chunk}`.slice(-8000); });
    child.on("error", (error) => fail(new Error(`Unable to start the rendering engine (${error.message}).`)));
    child.on("close", (code) => fail(new Error(`Rendering engine stopped${code ? ` with code ${code}` : ""}.`)));
    return child;
  }

  dispatch() {
    if (this.active || !this.queue.length) return;
    const pending = this.queue.shift();
    this.active = pending;
    let child;
    try { child = this.child || this.start(); }
    catch (error) { this.stop(error); return; }
    // Queued work gets its full timeout only after the previous job finishes.
    pending.timer = setTimeout(() => {
      this.stop(new Error("This operation took too long. Try again."));
    }, pending.timeoutMs);
    try {
      child.stdin.write(`${JSON.stringify({ id: pending.id, args: pending.args })}\n`, (error) => {
        if (error && this.child === child) this.stop(new Error(`Could not contact the rendering engine (${error.message}).`));
      });
    } catch (error) { this.stop(error); }
  }

  stop(error = new Error("Court Creator closed.")) {
    const child = this.child;
    this.child = null;
    const pending = this.active;
    this.active = null;
    if (pending) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    for (const queued of this.queue.splice(0)) queued.reject(error);
    if (child && !child.killed) child.kill();
  }
}

module.exports = { PythonWorker };
