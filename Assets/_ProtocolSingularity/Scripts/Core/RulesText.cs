namespace ProtocolSingularity.Core
{
    /// <summary>
    /// タイトル画面とロビーメニューの両方で表示されるルール本文。
    /// Rich-text (UI Toolkit) で色付き / 太字タグを含む。色は FactionColors と同系統。
    /// </summary>
    public static class RulesText
    {
        public const string Body =
@"<b>— 概要 —</b>
人類 vs AI の正体隠匿ゲーム (Avalon 系)。人類はハッキングで AI を排除、AI は内部から妨害して勝利する。

<b>— 勝利条件 —</b>
<color=#8CB8FF>人類陣営</color>: ハック 3 回成功 + OVERRIDE で AI が ORACLE 以外を指名
<color=#FF7878>AI 陣営</color>: 下記いずれか
  • ハック 3 回失敗
  • チーム提案が 5 回連続否決
  • OVERRIDE で ORACLE を正しく指名

<b>— 役職 —</b>
<color=#FFD84D>ORACLE</color>       — 全員の陣営が見える (CIPHER だけは盲点)
<color=#8CB8FF>ADMIN</color>        — ORACLE と MOTHER CORE を見分けられない
<color=#8CB8FF>OPERATOR</color>     — 能力なしの一般市民

<color=#FF7878>MOTHER CORE</color> — AI のリーダー。ADMIN には Oracle として偽装表示。OVERRIDE を主導
<color=#FF7878>AGENT</color>       — 一般 AI
<color=#FF7878>CIPHER</color>      — ORACLE の索引から除外された暗号化 AI
<color=#FF7878>DRONE</color>       — 序盤は自分を OPERATOR と誤認。ハック 2 回終了後に覚醒
<color=#FF7878>RADICAL</color>     — AI に与する人類側の改革派。AI からは OPERATOR に映り互いに孤立。OVERRIDE 時に AI 陣営と相互可視化される

<b>— ラウンドの流れ (全 3 ラウンド) —</b>
1. リーダー選出 (順番)
2. チーム提案 — リーダーがメンバーを指名
3. 承認投票 — 全員が公開投票。賛成 > 反対で可決
4. ハック実行 — メンバーが秘密裏に CLEAN / NOISE を提出
   ※ 人類と未覚醒 DRONE は CLEAN 固定
   ※ NOISE が規定数以上で失敗
5. 結果公開 — NOISE 枚数のみ開示 (誰が出したかは非公開)

<b>— ラウンド毎のチームサイズ / 失敗条件 —</b>
人数  R1  R2  R3   備考
  6   2   3   4
  7   2   3   3
  8   3   4   4    R3 は NOISE 2 枚以上で失敗
 9-10 3   4   5    R3 は NOISE 2 枚以上で失敗

<b>— OVERRIDE フェーズ —</b>
ハック 3 回成功で発動。AI 陣営全員 (RADICAL 含む) が相互に真の役職を認識し、秘密投票で 1 人を指名。MOTHER CORE が最終決定。対象が <color=#FFD84D>ORACLE</color> なら AI 逆転勝利、外せば人類勝利。

<b>— コミュニケーション —</b>
チャットは全体公開のみ (AI 陣営専用チャットは無し)
@名前 で他プレイヤーを参照可能";
    }
}
