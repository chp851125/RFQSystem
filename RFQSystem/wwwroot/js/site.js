// ===== 階梯報價（採購代登與供應商入口共用） =====

// 依階梯計算某數量的單價：取「起訂數量 ≤ 數量」的最高階梯；未達第一階以第一階計價
function tierPriceAt(tiers, qty) {
    tiers = tiers.filter(t => t.q > 0 && t.p > 0).sort((a, b) => a.q - b.q);
    if (!tiers.length) return null;
    let p = tiers[0].p;
    for (const t of tiers) if (qty >= t.q) p = t.p;
    return p;
}

function readTiers($editor) {
    return $editor.find('.tier-slot').map(function () {
        return { q: Number($(this).find('.tier-q').val() || 0), p: Number($(this).find('.tier-p').val() || 0) };
    }).get();
}

// 階梯有填寫時，「單價」改為自動帶入且不可手改；清空階梯則恢復手動輸入
function syncTierPrice($editor) {
    const line = $editor.data('line');
    const $form = $editor.closest('form');
    const $price = $form.find('.line-price[data-line="' + line + '"]');
    const p = tierPriceAt(readTiers($editor), Number($editor.data('qty')));
    if (p !== null) {
        $price.val(p).prop('readonly', true).addClass('bg-light').trigger('input');
        $editor.find('.tier-hint').text('（目前適用 ' + p.toLocaleString() + '）');
    } else {
        $price.prop('readonly', false).removeClass('bg-light');
        $editor.find('.tier-hint').text('');
    }
}

$(document).on('input', '.tier-q, .tier-p', function () { syncTierPrice($(this).closest('.tier-editor')); });

$(document).on('click', '.tier-toggle', function () {
    $(this).closest('form').find('.tier-row[data-line="' + $(this).data('line') + '"]').toggleClass('d-none');
});

$(function () {
    $('.tier-editor').each(function () { syncTierPrice($(this)); });
});
