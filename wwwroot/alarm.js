window.dropAlarm = {
  ctx: null,
  flashTimer: null,

  unlock() {
    try {
      this.ctx ??= new (window.AudioContext || window.webkitAudioContext)();
      this.ctx.resume();
    } catch { /* no audio available */ }
  },

  // A saved product just went live (softer, rising chirps).
  live(label) {
    this.unlock();
    const c = this.ctx;
    if (c) {
      for (let i = 0; i < 9; i++) {
        const t = c.currentTime + i * 0.18 + Math.floor(i / 3) * 0.4;
        const osc = c.createOscillator();
        const gain = c.createGain();
        osc.type = "triangle";
        osc.frequency.value = [660, 880, 1175][i % 3];
        gain.gain.setValueAtTime(0.0001, t);
        gain.gain.exponentialRampToValueAtTime(0.3, t + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.16);
        osc.connect(gain).connect(c.destination);
        osc.start(t);
        osc.stop(t + 0.18);
      }
    }
    this.flash("LIVE NOW — " + label);
    if ("Notification" in window && Notification.permission === "granted") {
      new Notification("Live on Target", { body: label });
    }
  },

  flash(text) {
    const original = "Drop Desk";
    let on = false, n = 0;
    clearInterval(this.flashTimer);
    this.flashTimer = setInterval(() => {
      document.title = (on = !on) ? text : original;
      if (++n > 40) { clearInterval(this.flashTimer); document.title = original; }
    }, 700);
  },

  ring(label) {
    this.unlock();
    const c = this.ctx;
    if (c) {
      for (let i = 0; i < 40; i++) {
        const t = c.currentTime + i * 0.45;
        const osc = c.createOscillator();
        const gain = c.createGain();
        osc.type = "square";
        osc.frequency.value = i % 2 ? 880 : 1320;
        gain.gain.setValueAtTime(0.0001, t);
        gain.gain.exponentialRampToValueAtTime(0.25, t + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.35);
        osc.connect(gain).connect(c.destination);
        osc.start(t);
        osc.stop(t + 0.4);
      }
    }

    // Flash the tab title so it stands out in the tab bar.
    this.flash("IN CART — CHECK OUT");

    if ("Notification" in window && Notification.permission === "granted") {
      new Notification("In your cart", { body: `${label}. Checkout is open in Chrome.` });
    }
  }
};

// Ask once for desktop notification permission on the first click anywhere.
document.addEventListener("click", () => {
  if ("Notification" in window && Notification.permission === "default") Notification.requestPermission();
}, { once: true });
