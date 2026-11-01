// Help mode: browsers never show title tooltips on touch screens, so a floating "?" button
// turns the whole page into "tap any item to see what it does". While it is on, tapping an
// item that has a tooltip shows that text in a bubble instead of activating the item.
// It reads the same title attributes the desktop tooltips use, so there is one text per item.
(function () {
  let active = false;
  let bubble = null;
  let hideTimer = null;

  const button = document.createElement('button');
  button.type = 'button';
  button.id = 'help-toggle';
  button.className = 'help-toggle';
  button.textContent = '?';
  // These strings are translated by the dashboard's applyTranslations() (text nodes and titles).
  button.title = 'Help: tap any item to see what it does';
  button.setAttribute('aria-label', 'Help');

  const banner = document.createElement('div');
  banner.id = 'help-banner';
  banner.className = 'help-banner hidden';
  banner.setAttribute('role', 'status');
  const bannerText = document.createElement('span');
  const done = document.createElement('button');
  done.type = 'button';
  done.className = 'btn-secondary';
  bannerText.textContent = 'Tap any item to see what it does.';
  done.textContent = 'Done';
  banner.append(bannerText, done);

  function hideBubble() {
    clearTimeout(hideTimer);
    if (bubble) { bubble.remove(); bubble = null; }
  }

  function showBubble(target, text) {
    hideBubble();
    bubble = document.createElement('div');
    bubble.className = 'help-bubble';
    bubble.setAttribute('role', 'tooltip');
    bubble.textContent = text;
    document.body.appendChild(bubble);

    // Place below the item (or above if there is no room), kept inside the viewport.
    const rect = target.getBoundingClientRect();
    const margin = 8;
    const width = Math.min(bubble.offsetWidth, window.innerWidth - margin * 2);
    let left = Math.max(margin, Math.min(rect.left, window.innerWidth - width - margin));
    let top = rect.bottom + 6;
    if (top + bubble.offsetHeight > window.innerHeight - margin) {
      top = Math.max(margin, rect.top - bubble.offsetHeight - 6);
    }
    bubble.style.left = left + 'px';
    bubble.style.top = top + 'px';
    hideTimer = setTimeout(hideBubble, 9000);
  }

  function setActive(on) {
    active = on;
    document.body.classList.toggle('help-active', on);
    banner.classList.toggle('hidden', !on);
    button.classList.toggle('on', on);
    button.setAttribute('aria-pressed', String(on));
    hideBubble();
  }

  // Anything that would DO something when tapped. In help mode all of these are blocked, so
  // an item without a description can never be triggered by accident.
  const INTERACTIVE = 'button, a[href], input, select, textarea, summary, label';

  function helpTarget(node) {
    if (!(node instanceof Element)) return null;
    if (node.closest('#help-toggle, #help-banner')) return null;
    return node.closest('[title]') || node.closest(INTERACTIVE);
  }

  // Capture phase, so the item's own click handler never runs while help mode is on.
  document.addEventListener('click', (event) => {
    if (!active) return;
    const target = helpTarget(event.target);
    if (!target) { hideBubble(); return; }
    event.preventDefault();
    event.stopPropagation();
    showBubble(target, target.getAttribute('title') || 'No description is available for this item.');
  }, true);

  // Stop dropdowns and text fields from opening/focusing while help mode is on.
  document.addEventListener('mousedown', (event) => {
    if (!active) return;
    const target = helpTarget(event.target);
    if (target && event.target.closest('select, input, textarea')) event.preventDefault();
  }, true);

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && active) setActive(false);
  });
  window.addEventListener('scroll', hideBubble, true);
  window.addEventListener('resize', hideBubble);

  button.addEventListener('click', () => setActive(!active));
  done.addEventListener('click', () => setActive(false));

  document.body.append(button, banner);
})();
