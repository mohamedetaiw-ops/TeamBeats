"use client";

import { useEffect, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { IconButton } from "@/components/ui/IconButton";
import { problemMessages } from "@/lib/notifications/types";

/** The recording formats the API accepts, in the order browsers are tried. */
const FORMATS = ["audio/webm;codecs=opus", "audio/webm", "audio/ogg;codecs=opus", "audio/mp4"] as const;

function recordingFormat(): string | null {
  if (typeof window === "undefined" || typeof MediaRecorder === "undefined") return null;
  return FORMATS.find((f) => MediaRecorder.isTypeSupported(f)) ?? null;
}

/**
 * Voice input (spec 014 US6 scenario 1, AST-016): the person presses the microphone, speaks, and presses it again
 * (or reaches the time limit). The API transcribes the audio through the AI Gateway and keeps nothing; the text is put
 * in the composer for the person to edit, and nothing is asked until they send it. Shown only when the API offers voice.
 */
export function VoiceInput({
  maxSeconds,
  disabled = false,
  onTranscript,
}: {
  maxSeconds: number;
  disabled?: boolean;
  onTranscript: (text: string) => void;
}) {
  const t = useTranslations("assistant.voice");
  const [state, setState] = useState<"idle" | "recording" | "transcribing">("idle");
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState("");
  const recorder = useRef<MediaRecorder | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
      recorder.current?.stream.getTracks().forEach((track) => track.stop());
    },
    [],
  );

  async function transcribe(audio: Blob) {
    setState("transcribing");
    setStatus(t("transcribing"));
    try {
      // The language is left to the speech model, since people ask in Arabic or English whatever the page language.
      const response = await fetch("/api/v1/assistant/transcribe", {
        method: "POST",
        headers: { "content-type": audio.type || "audio/webm" },
        body: audio,
      });
      if (!response.ok) {
        setError((await problemMessages(response)).join(" ") || t("failed"));
        setStatus("");
        return;
      }
      const { text } = (await response.json()) as { text: string };
      onTranscript(text);
      setStatus(t("ready"));
    } catch {
      setError(t("failed"));
      setStatus("");
    } finally {
      setState("idle");
    }
  }

  async function start() {
    setError(null);
    const format = recordingFormat();
    if (!format || !navigator.mediaDevices?.getUserMedia) {
      setError(t("unsupported"));
      return;
    }
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch {
      setError(t("permission"));
      return;
    }
    const chunks: Blob[] = [];
    const rec = new MediaRecorder(stream, { mimeType: format });
    rec.ondataavailable = (e) => {
      if (e.data.size > 0) chunks.push(e.data);
    };
    rec.onstop = () => {
      stream.getTracks().forEach((track) => track.stop());
      if (timer.current) clearTimeout(timer.current);
      recorder.current = null;
      const audio = new Blob(chunks, { type: format.split(";")[0] });
      if (audio.size === 0) {
        setState("idle");
        setError(t("empty"));
        return;
      }
      void transcribe(audio);
    };
    recorder.current = rec;
    rec.start();
    setState("recording");
    setStatus(t("recording", { seconds: maxSeconds }));
    timer.current = setTimeout(() => rec.state === "recording" && rec.stop(), maxSeconds * 1000);
  }

  function stop() {
    if (recorder.current?.state === "recording") recorder.current.stop();
  }

  return (
    <span className="assistant-voice">
      <IconButton
        icon={state === "recording" ? "stop" : "mic"}
        size="sm"
        variant={state === "recording" ? "primary" : "quiet"}
        label={state === "recording" ? t("stop") : t("start")}
        aria-pressed={state === "recording"}
        disabled={disabled || state === "transcribing"}
        onClick={() => (state === "recording" ? stop() : void start())}
      />
      <span className="visually-hidden" role="status">
        {status}
      </span>
      {error ? (
        <span className="field-error" role="alert">
          {error}
        </span>
      ) : null}
    </span>
  );
}

/**
 * Read aloud (spec 014 US6 scenario 2): the answer of this turn is spoken in its language through the AI Gateway. The
 * citations stay on screen; the audio is played from memory and never kept.
 */
export function ReadAloud({ turnId }: { turnId: string }) {
  const t = useTranslations("assistant.voice");
  const [state, setState] = useState<"idle" | "loading" | "playing">("idle");
  const [error, setError] = useState<string | null>(null);
  const player = useRef<HTMLAudioElement | null>(null);
  const url = useRef<string | null>(null);

  function release() {
    player.current?.pause();
    player.current = null;
    if (url.current) URL.revokeObjectURL(url.current);
    url.current = null;
  }

  useEffect(() => release, []);

  async function play() {
    setError(null);
    setState("loading");
    try {
      const response = await fetch("/api/v1/assistant/speak", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ turnId }),
      });
      if (!response.ok) {
        setError((await problemMessages(response)).join(" ") || t("failed"));
        setState("idle");
        return;
      }
      release();
      url.current = URL.createObjectURL(await response.blob());
      const audio = new Audio(url.current);
      audio.onended = () => {
        release();
        setState("idle");
      };
      player.current = audio;
      setState("playing");
      await audio.play().catch(() => {
        release();
        setState("idle");
        setError(t("playFailed"));
      });
    } catch {
      setError(t("failed"));
      setState("idle");
    }
  }

  return (
    <span className="assistant-voice">
      <IconButton
        icon={state === "playing" ? "stop" : "volume"}
        size="xs"
        label={state === "playing" ? t("stopReading") : state === "loading" ? t("preparing") : t("read")}
        aria-pressed={state === "playing"}
        disabled={state === "loading"}
        onClick={() => {
          if (state === "playing") {
            release();
            setState("idle");
          } else {
            void play();
          }
        }}
      />
      {error ? (
        <span className="field-error" role="alert">
          {error}
        </span>
      ) : null}
    </span>
  );
}
