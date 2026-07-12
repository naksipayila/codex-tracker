const { contextBridge, ipcRenderer } = require("electron")

contextBridge.exposeInMainWorld("codexUsage", {
  onUpdate(callback) {
    ipcRenderer.on("usage:update", (_, usage) => callback(usage))
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
  toggleWeekly() {
    return ipcRenderer.invoke("widget:toggle-weekly")
  },
})
