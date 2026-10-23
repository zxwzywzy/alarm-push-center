(function (root) {
  const esc = (value) => String(value).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
  function chart(points, series, rightMaximum, viewportWidth = 900) {
    if (points.length < 2) return '<div class="trend-empty">正在积累真实采样数据，约 5 秒后显示趋势。</div>';
    const width = Math.max(280, Math.min(1200, Number(viewportWidth) || 900));
    const height = width < 700 ? 190 : 235, left = 55, top = 15, right = 65, bottom = 38;
    const w = width - left - right, h = height - top - bottom;
    const number = (value) => Number.isFinite(Number(value)) ? Math.max(0, Number(value)) : 0;
    const leftMax = Math.max(1, ...points.flatMap((p) => series.filter((s) => !s.right).map((s) => number(p[s.key])))) * 1.15;
    const rightMax = Math.max(1, rightMaximum);
    const start = new Date(points[0].time).getTime(), end = new Date(points.at(-1).time).getTime();
    const x = (p) => left + Math.max(0, Math.min(1, (new Date(p.time).getTime() - start) / Math.max(1, end - start))) * w;
    const y = (p, s) => top + h - Math.min(1, number(p[s.key]) / (s.right ? rightMax : leftMax)) * h;
    const stamp = (time) => new Date(time).toLocaleTimeString('zh-CN', {hour12:false});
    let svg = `<svg viewBox="0 0 ${width} ${height}" role="img" aria-label="${esc(series.map((s) => s.label).join('、'))}趋势图"><title>${esc(series.map((s) => s.label).join('、'))}，从 ${stamp(start)} 到 ${stamp(end)}</title>`;
    for (let i = 0; i <= 4; i++) {
      const lineY = top + h * i / 4;
      svg += `<line x1="${left}" y1="${lineY}" x2="${left+w}" y2="${lineY}" stroke="#e2e9e0" stroke-width="1"/><text x="${left-8}" y="${lineY+4}" text-anchor="end">${(leftMax*(1-i/4)).toFixed(leftMax < 10 ? 1 : 0)}</text><text class="right-axis" x="${left+w+8}" y="${lineY+4}">${(rightMax*(1-i/4)).toFixed(rightMax === 100 ? 0 : 1)}</text>`;
    }
    for (let i = 0; i <= 4; i++) {
      const tickX = left + w*i/4;
      svg += `<text x="${tickX}" y="${height-9}" text-anchor="middle">${stamp(start+(end-start)*i/4)}</text>`;
    }
    for (const s of series) {
      const coordinates = points.map((p) => `${x(p).toFixed(1)},${y(p,s).toFixed(1)}`).join(' ');
      svg += `<polyline points="${coordinates}" fill="none" stroke="${s.color}" stroke-width="2.5" stroke-linejoin="round" ${s.right ? 'stroke-dasharray="6 5"' : ''}/>`;
      // Mark only 30 points per line on long time windows; every recorded value remains in the curve.
      for (const [i,p] of points.entries()) if (i % Math.max(1,Math.ceil(points.length/30)) === 0 || i === points.length-1)
        svg += `<circle cx="${x(p).toFixed(1)}" cy="${y(p,s).toFixed(1)}" r="3" fill="${s.color}"><title>${stamp(p.time)} ${esc(s.label)}：${number(p[s.key])}</title></circle>`;
    }
    return svg + '</svg>';
  }
  const api = { chart };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.TelemetryUI = api;
})(typeof window === 'undefined' ? globalThis : window);
