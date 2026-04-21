// Protocol Singularity — C# 側から IME ブリッジを制御するための jslib.
// 実体の実装は WebGL テンプレートの TemplateData/ime-bridge.js にあり、
// ここでは window.__imeBridge* 関数を C# から呼べるようラップするだけ。

mergeInto(LibraryManager.library, {

  ImeBridge_Enable: function() {
    if (typeof window.__imeBridgeEnable === "function") {
      window.__imeBridgeEnable();
    }
  },

  ImeBridge_Disable: function() {
    if (typeof window.__imeBridgeDisable === "function") {
      window.__imeBridgeDisable();
    }
  },

  ImeBridge_IsAvailable: function() {
    return (typeof window.__imeBridgeEnable === "function") ? 1 : 0;
  },

});
