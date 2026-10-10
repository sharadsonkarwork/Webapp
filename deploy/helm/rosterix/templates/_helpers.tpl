{{- define "rosterix.labels" -}}
app.kubernetes.io/name: rosterix
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end -}}
