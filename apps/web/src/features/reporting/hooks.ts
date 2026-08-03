import { useQuery } from "@tanstack/react-query";
import { queryKeys } from "../../api/queryKeys";
import { getBookingFunnel, getBookingOverview, type ReportingWindow } from "./api";

export function useBookingFunnel(window: ReportingWindow) {
  return useQuery({
    queryKey: queryKeys.reporting.funnel(window.from, window.to),
    queryFn: ({ signal }) => getBookingFunnel(window, signal),
  });
}

export function useBookingOverview(window: ReportingWindow) {
  return useQuery({
    queryKey: queryKeys.reporting.overview(window.from, window.to),
    queryFn: ({ signal }) => getBookingOverview(window, signal),
  });
}
