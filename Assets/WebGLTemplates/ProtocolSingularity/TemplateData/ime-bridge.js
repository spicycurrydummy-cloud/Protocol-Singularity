// Protocol Singularity — WebGL IME bridge (v2).
//
// <canvas> 要素は DOM のテキスト入力イベント (compositionstart / compositionend) を
// 受け取れないため、Unity の UI Toolkit TextField にフォーカスが当たった瞬間に
// 隠し <textarea> へ DOM フォーカスを流して IME 入力を捕まえる。
// IME 確定文字列は unityInstance.SendMessage("ImeBridge", "ReceiveImeText", text) で
// Unity 側へ渡す (ImeBridge.cs 受信)。
//
// 有効化は C# 側の ImeBridge.jslib 経由で以下のいずれかを呼ぶ:
//   - window.__imeBridgeEnable()  : UI Toolkit TextField focus in で呼ぶ
//   - window.__imeBridgeDisable() : UI Toolkit TextField focus out で呼ぶ

(function () {
  var bridge = document.getElementById("ime-bridge");
  var canvas = document.getElementById("unity-canvas");
  if (!bridge || !canvas) return;

  var unityInstance = null;
  var targetObject = "ImeBridge";
  var targetMethod = "ReceiveImeText";
  var submitMethod = "ReceiveImeSubmit";
  var composing = false;

  function send(text) {
    if (!unityInstance || !text) return;
    try { unityInstance.SendMessage(targetObject, targetMethod, text); } catch (e) {}
  }

  function sendSubmit() {
    if (!unityInstance) return;
    try { unityInstance.SendMessage(targetObject, submitMethod, ""); } catch (e) {}
  }

  bridge.addEventListener("compositionstart", function () {
    composing = true;
  });

  bridge.addEventListener("compositionend", function (e) {
    composing = false;
    var text = e.data || bridge.value;
    bridge.value = "";
    if (text) send(text);
  });

  // 直接入力 (半角) も拾う
  bridge.addEventListener("input", function () {
    if (composing) return;
    var text = bridge.value;
    bridge.value = "";
    if (text) send(text);
  });

  // Enter キーで送信トリガー (JP 入力の確定 Enter とは別。変換中は compositionend のみ発火)
  bridge.addEventListener("keydown", function (e) {
    if (composing || e.isComposing) return;
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      sendSubmit();
    }
  });

  bridge.addEventListener("blur", function () {
    composing = false;
  });

  // C# 側からの指示を受けて textarea にフォーカスを移す (= IME 受付開始)
  window.__imeBridgeEnable = function () {
    bridge.style.pointerEvents = "auto";
    try { bridge.focus({ preventScroll: true }); } catch (e) { bridge.focus(); }
  };

  // 逆に canvas にフォーカスを戻す (= IME 受付終了)
  window.__imeBridgeDisable = function () {
    bridge.blur();
    try { canvas.focus({ preventScroll: true }); } catch (e) { canvas.focus(); }
  };

  // createUnityInstance 完了後に呼ばれる
  window.__imeBridgeAttach = function (instance) {
    unityInstance = instance;
  };
})();
