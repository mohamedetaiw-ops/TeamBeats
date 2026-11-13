"use client";

import type { ReactNode } from "react";
import { NextIntlClientProvider, type AbstractIntlMessages } from "next-intl";
import { messageFallback, onIntlError } from "@/i18n/fallback";

/** The client side of the messages, with the same missing message fallback as the server (spec 007 FR-030). */
export function IntlProvider({ locale, messages, timeZone, children }: { locale: string; messages: AbstractIntlMessages; timeZone?: string; children: ReactNode }) {
  return (
    <NextIntlClientProvider locale={locale} messages={messages} timeZone={timeZone} getMessageFallback={messageFallback} onError={onIntlError}>
      {children}
    </NextIntlClientProvider>
  );
}
