// Protocol Singularity — C# 側から IME overlay を制御するための jslib.
// 実装は WebGL テンプレートの TemplateData/ime-bridge.js にあり、window.__imeBridge*
// 関数をラップしている。

mergeInto(LibraryManager.library, {

  ImeBridge_Show: function() {
    if (typeof window.__imeBridgeShow === "function") window.__imeBridgeShow();
  },

  ImeBridge_Hide: function() {
    if (typeof window.__imeBridgeHide === "function") window.__imeBridgeHide();
  },

  ImeBridge_SetValue: function(ptr) {
    var s = UTF8ToString(ptr);
    if (typeof window.__imeBridgeSetValue === "function") window.__imeBridgeSetValue(s);
  },

  ImeBridge_Insert: function(ptr) {
    var s = UTF8ToString(ptr);
    if (typeof window.__imeBridgeInsert === "function") window.__imeBridgeInsert(s);
  },

  ImeBridge_IsAvailable: function() {
    return (typeof window.__imeBridgeShow === "function") ? 1 : 0;
  },

});
