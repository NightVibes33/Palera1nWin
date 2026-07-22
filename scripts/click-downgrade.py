import time

import pyautogui
import pygetwindow as gw


window = gw.getWindowsWithTitle("Palera1nWin")[0]
window.activate()
window.moveTo(0, 0)
window.resizeTo(1900, 1000)
time.sleep(1)

# Put keyboard focus on the downgrade page and move to its final action area.
pyautogui.click(900, 500)
pyautogui.press("end")
pyautogui.press("pagedown", presses=20, interval=0.05)
time.sleep(1)
pyautogui.screenshot(
    r"C:\Users\bobby\Downloads\Palera1nWin-darksword-restore\elevated-actions.png"
)

# Open the final action panel.
pyautogui.click(1535, 142)
time.sleep(1)
pyautogui.screenshot(
    r"C:\Users\bobby\Downloads\Palera1nWin-darksword-restore\elevated-review.png"
)
