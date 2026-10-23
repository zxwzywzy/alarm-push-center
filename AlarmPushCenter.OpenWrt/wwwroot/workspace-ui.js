/* UI composition only. Collection, dialing and notification logic stay in their original modules. */
(() => {
  const icons = {
    overview: '<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><path d="M14 20v-4m3.5 4v-7m3.5 7v-10"/>',
    device: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8m-4-4v4M6 11h3l2-4 3 7 2-3h2"/>',
    notification: '<path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4"/>',
    settings: '<path d="M3 6h3m4 0h11M3 12h11m4 0h3M3 18h5m4 0h9"/><circle cx="8" cy="6" r="2"/><circle cx="16" cy="12" r="2"/><circle cx="10" cy="18" r="2"/>',
  };
  for (const node of document.querySelectorAll('[data-icon]'))
    node.innerHTML = `<svg width="21" height="21" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">${icons[node.dataset.icon] || ''}</svg>`;

  const descriptions = {
    service: ['采集服务', '查看运行状态，决定何时开始采集。'],
    security: ['账户与安全', '管理登录密码、邮箱找回和启动偏好。'],
    mobile: ['手机连接', '扫码授权手机 App，只连接报警插件。'],
    gateway: ['随身 WiFi 电话网关', '网关配置一次，提醒条件在各台设备中单独设置。'],
    advanced: ['高级与帮助', '查看配置诊断、使用指引和风险说明。'],
    updates: ['版本更新', '检查、备份与更新，保留当前设备配置和手机授权。'],
  };
  let section = 'service';
  const grid = document.querySelector('#system .settings-grid');
  function classify() {
    for (const article of grid.children) {
      if (article.querySelector('#mobile-pair-create')) article.dataset.settingsSection = 'mobile';
      if (article.classList.contains('phone-gateway')) article.dataset.settingsSection = 'gateway';
      article.hidden = article.dataset.settingsSection !== section;
    }
  }
  function selectSettings(key) {
    if (!descriptions[key]) return;
    section = key;
    const [title, description] = descriptions[key];
    document.querySelector('#settings-title').textContent = title;
    document.querySelector('#settings-description').textContent = description;
    for (const button of document.querySelectorAll('.settings-navigation button')) {
      const active = button.dataset.settings === key;
      button.classList.toggle('active', active);
      button.setAttribute('aria-pressed', String(active));
    }
    classify();
  }
  // The QR module mounts asynchronously. Classify only newly mounted direct cards.
  new MutationObserver(classify).observe(grid, { childList: true });
  const gateway = document.querySelector('.phone-gateway');
  if (gateway) grid.append(gateway);
  classify();
  document.addEventListener('click', event => {
    const button = event.target.closest('[data-settings]');
    if (button) selectSettings(button.dataset.settings);
    if (event.target.closest('#open-phone-gateway')) selectSettings('gateway');
    const start = event.target.closest('[data-start]');
    if (start) document.getElementById('add-' + start.dataset.start)?.click();
  });
  for (const id of ['login-form', 'password-form', 'console-settings-form'])
    enhanceSecrets(document.getElementById(id));
  document.querySelector('[name=recovery_enabled]').addEventListener('change', event => {
    if (event.target.checked) document.querySelector('#recovery-mail-settings').open = true;
  });

  // Keep protected confirmations operable without a mouse and return focus on close.
  const modal = document.querySelector('#modal');
  let returnFocus;
  new MutationObserver(() => {
    if (modal.classList.contains('hidden')) {
      if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
      returnFocus = null;
    } else if (!modal.contains(document.activeElement)) {
      returnFocus = document.activeElement;
      document.querySelector('#modal-cancel').focus();
    }
  }).observe(modal, { attributes: true, attributeFilter: ['class'] });
  modal.addEventListener('keydown', event => {
    if (event.key !== 'Tab') return;
    const first = document.querySelector('#modal-cancel'), last = document.querySelector('#modal-ok');
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  });
})();
