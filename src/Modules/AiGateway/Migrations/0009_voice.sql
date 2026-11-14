-- Spec 014 R2 (FR-026): spoken questions and answers read aloud go through the gateway as the use cases transcribe and
-- speak, served by models with the transcription and speech capabilities. Audio is sent for the call and never stored.
alter table ai.platform_route drop constraint platform_route_use_case_check;
alter table ai.platform_route add constraint platform_route_use_case_check
  check (use_case in ('assistant', 'summarize', 'translate', 'draft', 'embed', 'agent', 'evaluation', 'transcribe', 'speak'));

-- Starting entries the platform operator keeps current. Speech to text is priced by the minute at the provider; the
-- per token price here is an estimate for budgets until the operator records the real one.
insert into ai.model_catalogue (model, provider, names, capabilities, context_tokens, regions, input_price, output_price) values
  ('openai/whisper-1', 'openrouter', '{"en": "Whisper"}', '{transcription}', 32000, '{us}', 0, 6.00),
  ('openai/gpt-4o-mini-tts', 'openrouter', '{"en": "GPT-4o mini TTS"}', '{speech}', 2000, '{us}', 0.60, 12.00)
on conflict (model) do nothing;

insert into ai.platform_route (use_case, models) values
  ('transcribe', '{openai/whisper-1}'),
  ('speak', '{openai/gpt-4o-mini-tts}')
on conflict (use_case) do nothing;
