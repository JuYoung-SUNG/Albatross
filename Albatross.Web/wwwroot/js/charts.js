// Blazor WASM <-> Chart.js interop 헬퍼
// canvas id별로 Chart 인스턴스를 보관해두고, 같은 id로 다시 그리면 기존 차트를 파괴 후 재생성한다.
window.albatrossCharts = (function () {
    const instances = {};

    function cssVar(name, fallback) {
        const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        return value || fallback;
    }

    // 데이터셋의 backgroundColor로 '$accent'/'$accent2' 토큰을 넘기면
    // 현재 선택된 테마의 --accent/--accent-2 값으로 해석한다 (테마 전환 시에도 항상 맞는 색을 쓰기 위함).
    function resolveColor(value) {
        if (value === '$accent') return cssVar('--accent', '#2563eb');
        if (value === '$accent2') return cssVar('--accent-2', '#16a34a');
        return value;
    }

    // n개의 선을 구분하기 좋은 색상 팔레트 (색상환을 균등 분할)
    function palette(n) {
        const colors = [];
        for (let i = 0; i < n; i++) {
            const hue = Math.round((360 / Math.max(n, 1)) * i);
            colors.push(`hsl(${hue}, 62%, 42%)`);
        }
        return colors;
    }

    function resolveBarDatasets(datasets) {
        return datasets.map(ds => ({
            ...ds,
            backgroundColor: resolveColor(ds.backgroundColor)
        }));
    }

    function resolveLineDatasets(datasets) {
        const colors = palette(datasets.length);
        return datasets.map((ds, i) => {
            const color = ds.borderColor ? resolveColor(ds.borderColor) : colors[i];
            return {
                ...ds,
                borderColor: color,
                backgroundColor: color,
                fill: false,
                tension: 0.25,
                pointRadius: 2,
                pointHoverRadius: 4,
                spanGaps: true
            };
        });
    }

    function destroy(canvasId) {
        if (instances[canvasId]) {
            instances[canvasId].destroy();
            delete instances[canvasId];
        }
    }

    function renderBar(canvasId, labels, datasets, options) {
        destroy(canvasId);
        const ctx = document.getElementById(canvasId);
        if (!ctx) return;

        const textColor = cssVar('--text-primary', '#0f172a');
        const mutedColor = cssVar('--text-muted', '#64748b');
        const gridColor = cssVar('--border', '#e2e8f0');

        instances[canvasId] = new Chart(ctx, {
            type: 'bar',
            data: { labels: labels, datasets: resolveBarDatasets(datasets) },
            options: Object.assign({
                responsive: true,
                maintainAspectRatio: false,
                plugins: { legend: { labels: { color: textColor } } },
                scales: {
                    x: { ticks: { color: mutedColor }, grid: { color: gridColor } },
                    y: { ticks: { color: mutedColor }, grid: { color: gridColor }, beginAtZero: true }
                }
            }, options || {})
        });
    }

    // y축을 0부터 시작하면 값이 좁은 범위에 몰린 지표(출루율 0.3~0.44 등)는 차트 대부분이 빈 공간이 된다 —
    // 실제 데이터의 최소~최대에 5% 여백만 붙여 축 범위를 데이터에 딱 맞춘다 (모든 값이 양수면 0 아래로는 안 내려감).
    function computeYBounds(datasets) {
        const vals = [];
        for (const ds of datasets) {
            for (const v of (ds.data || [])) {
                if (v !== null && v !== undefined && !isNaN(v)) vals.push(v);
            }
        }
        if (!vals.length) return {};
        const mn = Math.min.apply(null, vals);
        const mx = Math.max.apply(null, vals);
        const pad = (mx - mn) * 0.05 || Math.max(Math.abs(mx) * 0.02, 1);
        return {
            min: mn >= 0 ? Math.max(0, mn - pad) : mn - pad,
            max: mx + pad
        };
    }

    function renderLine(canvasId, labels, datasets, options) {
        destroy(canvasId);
        const ctx = document.getElementById(canvasId);
        if (!ctx) return;

        const textColor = cssVar('--text-primary', '#0f172a');
        const mutedColor = cssVar('--text-muted', '#64748b');
        const gridColor = cssVar('--border', '#e2e8f0');
        const yBounds = computeYBounds(datasets);

        instances[canvasId] = new Chart(ctx, {
            type: 'line',
            data: { labels: labels, datasets: resolveLineDatasets(datasets) },
            options: Object.assign({
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: 'nearest', intersect: false },
                plugins: {
                    legend: {
                        display: true,
                        position: 'bottom',
                        labels: { color: textColor, boxWidth: 12, font: { size: 10.5 } }
                    },
                    // 마우스 휠/터치 핀치로 확대, 드래그로 좌우 이동. 더블클릭하면 원래 배율로 복귀.
                    zoom: {
                        zoom: {
                            wheel: { enabled: true },
                            pinch: { enabled: true },
                            mode: 'x'
                        },
                        pan: { enabled: true, mode: 'x' },
                        limits: { x: { minRange: 2 } }
                    }
                },
                scales: {
                    x: { ticks: { color: mutedColor }, grid: { color: gridColor } },
                    y: Object.assign({ ticks: { color: mutedColor }, grid: { color: gridColor } }, yBounds)
                }
            }, options || {})
        });

        ctx.ondblclick = function () {
            const chart = instances[canvasId];
            if (chart && typeof chart.resetZoom === 'function') chart.resetZoom();
        };
    }

    return { renderBar, renderLine, destroy };
})();
