"use client";

import { useEffect, useId, useRef, useState } from "react";
import type { ButtonHTMLAttributes, PointerEvent as ReactPointerEvent } from "react";
import {
  useCopilotChatConfiguration,
  type ToolsMenuItem,
} from "@copilotkit/react-core/v2";

type AttachmentMenuButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  toolsMenu?: (ToolsMenuItem | "-")[];
  onAddFile?: () => void;
};

type FlatMenuItem = {
  label: string;
  action?: () => void;
};

function flattenMenuItems(items: (ToolsMenuItem | "-")[] | undefined): FlatMenuItem[] {
  if (!items) return [];

  const flattened: FlatMenuItem[] = [];
  for (const item of items) {
    if (item === "-") continue;
    if (item.items) {
      flattened.push(...flattenMenuItems(item.items));
    } else {
      flattened.push({ label: item.label, action: item.action });
    }
  }
  return flattened;
}

export function AttachmentMenuButton({
  className,
  toolsMenu,
  onAddFile,
  disabled,
  ...buttonProps
}: AttachmentMenuButtonProps) {
  const labels = useCopilotChatConfiguration()?.labels;
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const menuId = useId();

  const menuItems: FlatMenuItem[] = [
    ...(onAddFile
      ? [{ label: labels?.chatInputToolbarAddButtonLabel ?? "Add attachments", action: onAddFile }]
      : []),
    ...flattenMenuItems(toolsMenu),
  ];

  useEffect(() => {
    if (!open) return;

    const handlePointerDown = (event: PointerEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        setOpen(false);
        triggerRef.current?.focus();
      }
    };

    document.addEventListener("pointerdown", handlePointerDown);
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      document.removeEventListener("pointerdown", handlePointerDown);
      document.removeEventListener("keydown", handleKeyDown);
    };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    requestAnimationFrame(() => {
      menuRef.current?.querySelector<HTMLButtonElement>("[role=menuitem]")?.focus();
    });
  }, [open]);

  function toggleMenu(event: ReactPointerEvent<HTMLButtonElement>) {
    event.preventDefault();
    event.stopPropagation();
    if (!disabled && menuItems.length > 0) setOpen((current) => !current);
  }

  function runMenuItem(item: FlatMenuItem) {
    item.action?.();
    setOpen(false);
    triggerRef.current?.focus();
  }

  return (
    <div ref={containerRef} style={{ position: "relative" }}>
      <button
        {...buttonProps}
        ref={triggerRef}
        type="button"
        disabled={disabled || menuItems.length === 0}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        data-testid="copilot-add-menu-button"
        className={className}
        onClick={toggleMenu}
        style={{
          display: "inline-flex",
          alignItems: "center",
          justifyContent: "center",
          width: 36,
          height: 36,
          marginLeft: 4,
          borderRadius: "9999px",
          color: "inherit",
          cursor: disabled || menuItems.length === 0 ? "not-allowed" : "pointer",
          opacity: disabled || menuItems.length === 0 ? 0.5 : 1,
          ...buttonProps.style,
        }}
      >
        <span aria-hidden="true" style={{ fontSize: 24, lineHeight: 1, fontWeight: 300 }}>
          +
        </span>
      </button>

      {open && (
        <div
          id={menuId}
          ref={menuRef}
          role="menu"
          aria-label="Attachment actions"
          style={{
            position: "absolute",
            left: 0,
            bottom: "calc(100% + 8px)",
            zIndex: 1301,
            minWidth: 190,
            padding: 4,
            border: "1px solid #404040",
            borderRadius: 8,
            background: "#1f1f1f",
            color: "#f5f5f5",
            boxShadow: "0 10px 30px rgba(0, 0, 0, 0.45)",
          }}
        >
          {menuItems.map((item, index) => (
            <button
              key={`${item.label}-${index}`}
              type="button"
              role="menuitem"
              onClick={() => runMenuItem(item)}
              style={{
                display: "block",
                width: "100%",
                padding: "8px 10px",
                border: 0,
                borderRadius: 5,
                background: "transparent",
                color: "inherit",
                textAlign: "left",
                cursor: "pointer",
                fontSize: 14,
              }}
              onMouseEnter={(event) => {
                event.currentTarget.style.background = "#333333";
              }}
              onMouseLeave={(event) => {
                event.currentTarget.style.background = "transparent";
              }}
            >
              {item.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
