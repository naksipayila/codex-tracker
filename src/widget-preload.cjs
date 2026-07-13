const { contextBridge, ipcRenderer } = require("electron")

contextBridge.exposeInMainWorld("codexUsage", {
  onUpdate(callback) {
    ipcRenderer.on("usage:update", (_, usage) => callback(usage))
  },
  onAnimateIn(callback) {
    ipcRenderer.once("widget:animate-in", (_, enabled) => callback(enabled))
  },
  showContextMenu() {
    ipcRenderer.send("widget:context-menu")
  },
  startDrag(screenX) {
    ipcRenderer.send("widget:drag-start", screenX)
  },
  moveDrag(screenX) {
    ipcRenderer.send("widget:drag-move", screenX)
  },
  endDrag() {
    ipcRenderer.send("widget:drag-end")
  },
  getWeeklyCollapsed() {
    return ipcRenderer.invoke("widget:get-weekly-collapsed")
  },
  toggleWeekly(collapsed, animate) {
    return ipcRenderer.invoke("widget:toggle-weekly", collapsed, animate)
  },
  onWeeklyCollapsed(callback) {
    ipcRenderer.on("widget:weekly-collapsed", (_, collapsed) => callback(collapsed))
  },
})
