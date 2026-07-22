using System.Text.Json;

namespace InterviewGptBridge.Services;

public static class ChatGptDomBridge
{
    public const string ProbeReadyScript =
        """
        (() => {
          const selectors = [
            '#prompt-textarea',
            '[data-testid="prompt-textarea"]',
            'textarea',
            'div[contenteditable="true"]',
            '[role="textbox"][contenteditable="true"]'
          ];

          const isVisible = (element) => {
            const rect = element.getBoundingClientRect();
            const style = window.getComputedStyle(element);
            return rect.width > 20 && rect.height > 20 && style.visibility !== 'hidden' && style.display !== 'none';
          };

          if (!location.hostname.includes('chatgpt.com')) {
            return false;
          }

          if (location.pathname.includes('/auth') || location.pathname.includes('/login')) {
            return false;
          }

          return selectors
            .flatMap((selector) => Array.from(document.querySelectorAll(selector)))
            .some(isVisible);
        })();
        """;

    public static string BuildSubmitScript(string text)
    {
        var jsonText = JsonSerializer.Serialize(text);

        return $$"""
        (async () => {
          const text = {{jsonText}};

          const isVisible = (element) => {
            if (!element) return false;
            const rect = element.getBoundingClientRect();
            const style = window.getComputedStyle(element);
            return rect.width > 20 && rect.height > 20 && style.visibility !== 'hidden' && style.display !== 'none';
          };

          const promptSelectors = [
            '#prompt-textarea',
            '[data-testid="prompt-textarea"]',
            'textarea',
            'div[contenteditable="true"]',
            '[role="textbox"][contenteditable="true"]'
          ];

          const findPrompt = () => {
            const candidates = promptSelectors
              .flatMap((selector) => Array.from(document.querySelectorAll(selector)))
              .filter(isVisible);
            return candidates[candidates.length - 1] || null;
          };

          const dispatchInput = (element, data) => {
            element.dispatchEvent(new InputEvent('beforeinput', {
              bubbles: true,
              cancelable: true,
              inputType: 'insertText',
              data
            }));
            element.dispatchEvent(new InputEvent('input', {
              bubbles: true,
              inputType: 'insertText',
              data
            }));
            element.dispatchEvent(new Event('change', { bubbles: true }));
          };

          const prompt = findPrompt();
          if (!prompt) {
            return { ok: false, reason: 'ChatGPT prompt not found' };
          }

          prompt.focus();

          if (prompt instanceof HTMLTextAreaElement || prompt instanceof HTMLInputElement) {
            const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(prompt), 'value')?.set;
            if (setter) {
              setter.call(prompt, text);
            } else {
              prompt.value = text;
            }
            dispatchInput(prompt, text);
          } else {
            const selection = window.getSelection();
            const range = document.createRange();
            range.selectNodeContents(prompt);
            selection.removeAllRanges();
            selection.addRange(range);

            let inserted = false;
            try {
              inserted = document.execCommand('insertText', false, text);
            } catch {
              inserted = false;
            }

            if (!inserted) {
              prompt.textContent = text;
            }

            dispatchInput(prompt, text);
          }

          await new Promise((resolve) => window.requestAnimationFrame(resolve));

          const sendButtons = Array.from(document.querySelectorAll('button'))
            .filter((button) => isVisible(button) && !button.disabled)
            .filter((button) => {
              const testId = (button.getAttribute('data-testid') || '').toLowerCase();
              const label = (button.getAttribute('aria-label') || button.title || button.textContent || '').toLowerCase();
              return testId.includes('send') ||
                label.includes('send') ||
                label.includes('submit') ||
                label.includes('ask');
            });

          const sendButton = sendButtons[sendButtons.length - 1];
          if (sendButton) {
            sendButton.click();
            return { ok: true, reason: 'Clicked send button' };
          }

          prompt.dispatchEvent(new KeyboardEvent('keydown', {
            key: 'Enter',
            code: 'Enter',
            keyCode: 13,
            which: 13,
            bubbles: true,
            cancelable: true
          }));
          prompt.dispatchEvent(new KeyboardEvent('keyup', {
            key: 'Enter',
            code: 'Enter',
            keyCode: 13,
            which: 13,
            bubbles: true,
            cancelable: true
          }));

          return { ok: true, reason: 'Sent Enter key event' };
        })();
        """;
    }
}
